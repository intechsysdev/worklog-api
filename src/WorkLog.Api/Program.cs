using System.Security.Claims;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.OpenApi;
using Scalar.AspNetCore;
using Serilog;
using WorkLog.Api.Auth;
using WorkLog.Api.Endpoints;
using WorkLog.Api.Extensions;
using WorkLog.Application;
using WorkLog.Application.Abstractions;
using WorkLog.Infrastructure;
using WorkLog.Infrastructure.Persistence;
using WorkLog.Infrastructure.Persistence.Seed;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext());

// ── Núcleo y adaptadores ────────────────────────────────────────────────────
builder.Services.AddWorkLogApplication();
builder.Services.AddWorkLogInfrastructure(builder.Configuration);

builder.Services.AddMemoryCache();
builder.Services.AddScoped<RequestContext>();
builder.Services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<RequestContext>());
builder.Services.AddScoped<ICurrentActor>(sp => sp.GetRequiredService<RequestContext>());
builder.Services.AddScoped<ICallerCredentials>(sp => sp.GetRequiredService<RequestContext>());

// ── Autenticación: INTECHSYS ONE valida cada token ──────────────────────────
var authMode = builder.Configuration["Auth:Mode"] ?? "One";
if (string.Equals(authMode, "Development", StringComparison.OrdinalIgnoreCase))
{
    // Nunca fuera de Development: con este modo cualquiera entra escribiendo un correo.
    if (!builder.Environment.IsDevelopment())
        throw new InvalidOperationException("Auth:Mode = Development solo se permite en el entorno Development.");

    builder.Services.AddSingleton<IIdentityProvider, DevelopmentIdentityProvider>();
}

builder.Services.AddAuthentication(OneClaims.Scheme)
    .AddScheme<AuthenticationSchemeOptions, OneAuthenticationHandler>(OneClaims.Scheme, null);
builder.Services.AddAuthorization();
builder.Services.AddHostedService<WorkLog.Api.Jobs.WorkdayAutomationJob>();

// ── HTTP ────────────────────────────────────────────────────────────────────
const string CorsPolicy = "worklog-clients";
var origins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? ["http://localhost:5173"];
var patterns = builder.Configuration.GetSection("Cors:AllowedOriginPatterns").Get<string[]>() ?? [];

builder.Services.AddCors(options => options.AddPolicy(CorsPolicy, policy => policy
    .WithOrigins(origins.Concat(patterns).Where(o => !string.IsNullOrWhiteSpace(o)).Select(o => o.TrimEnd('/')).Distinct().ToArray())
    .SetIsOriginAllowedToAllowWildcardSubdomains()
    .AllowAnyHeader()
    .AllowAnyMethod()));

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // Por persona (o por IP si no hay sesión): una app con un error en bucle no tumba el API.
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http =>
        RateLimitPartition.GetTokenBucketLimiter(
            http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? http.Connection.RemoteIpAddress?.ToString() ?? "anon",
            _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = 300,
                TokensPerPeriod = 150,
                ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));

    options.OnRejected = async (context, ct) =>
    {
        context.HttpContext.Response.Headers.RetryAfter = "60";
        await context.HttpContext.Response.WriteAsJsonAsync(new
        {
            title = "Demasiadas solicitudes",
            status = 429,
            detail = "Superaste el límite de peticiones. Inténtalo de nuevo en un minuto."
        }, ct);
    };
});

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddHealthChecks().AddDbContextCheck<WorkLogDbContext>("sql-server");

builder.Services.ConfigureHttpJsonOptions(options =>
{
    // Enums como texto: los clientes no dependen de valores numéricos.
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    // App Service termina TLS en su balanceador: sin esto la IP real (y el límite por IP) se pierde.
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

builder.Services.AddOpenApi("v1", options => options.AddDocumentTransformer((document, _, _) =>
{
    document.Info = new OpenApiInfo
    {
        Title = "WorkLog · Bitácora de actividades",
        Version = "v1",
        Description = "API de WorkLog (INTECHSYS ONE): jornada y actividades de campo, horarios, equipo y configuración. " +
                      "Autenticación con el token de ONE (Bearer) y la empresa en la cabecera X-Tenant-Id."
    };

    document.Components ??= new OpenApiComponents();
    document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
    document.Components.SecuritySchemes["bearer"] = new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        Description = "Token de INTECHSYS ONE (SSO del portal o /api/v1/auth/login de ONE)."
    };
    return Task.CompletedTask;
}));

var app = builder.Build();

app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseSerilogRequestLogging(options => options.GetLevel = (http, _, ex) =>
    ex is not null || http.Response.StatusCode >= 500
        ? Serilog.Events.LogEventLevel.Error
        : http.Request.Path.StartsWithSegments("/health")
            ? Serilog.Events.LogEventLevel.Verbose
            : Serilog.Events.LogEventLevel.Information);

if (!app.Environment.IsDevelopment()) app.UseHttpsRedirection();

app.UseCors(CorsPolicy);
app.UseAuthentication();
app.UseRateLimiter();
app.UseMiddleware<TenantResolutionMiddleware>();
app.UseAuthorization();

app.MapOpenApi();
app.MapScalarApiReference(options => options
    .WithTitle("WorkLog · API")
    .WithTheme(ScalarTheme.DeepSpace)
    .EnableDarkMode()
    .AddPreferredSecuritySchemes("bearer"));

app.MapHealthChecks("/health").AllowAnonymous();

app.MapMeEndpoints();
app.MapPanelEndpoints();
app.MapPhase2Endpoints();

// Migraciones y datos del producto antes de aceptar tráfico: cada despliegue deja la base al día.
using (var scope = app.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("WorkLog.Startup");
    try
    {
        logger.LogInformation("Aplicando migraciones y datos iniciales…");
        await scope.ServiceProvider.GetRequiredService<DbSeeder>().RunAsync();
        logger.LogInformation("Base de datos lista.");
    }
    catch (Exception ex)
    {
        logger.LogCritical(ex, "No se pudo preparar la base de datos. Se aborta el arranque.");
        throw;
    }
}

await app.RunAsync();

/// <summary>Punto de entrada; público para las pruebas de integración.</summary>
public partial class Program;
