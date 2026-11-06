using Microsoft.Extensions.Caching.Memory;
using WorkLog.Application.Abstractions;
using WorkLog.Application.Access;
using WorkLog.Domain.Auditing;

namespace WorkLog.Api.Auth;

/// <summary>
/// Resuelve la empresa (cabecera X-Tenant-Id) y la persona de WorkLog para cada petición
/// autenticada. La empresa debe ser una de las que ONE reporta con WorkLog asignado.
/// </summary>
public sealed class TenantResolutionMiddleware(RequestDelegate next, IMemoryCache cache, ILogger<TenantResolutionMiddleware> logger)
{
    public const string TenantHeader = "X-Tenant-Id";
    public const string ClientHeader = "X-Client";

    private static readonly TimeSpan TenantsCacheFor = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ActorCacheFor = TimeSpan.FromSeconds(20);

    public async Task InvokeAsync(HttpContext http, RequestContext context, IIdentityProvider identities, IAccessService access)
    {
        context.SetOrigin(string.Equals(http.Request.Headers[ClientHeader], "mobile", StringComparison.OrdinalIgnoreCase)
            ? AuditOrigin.Mobile
            : AuditOrigin.Web);

        var endpoint = http.GetEndpoint();
        var needsTenant = endpoint?.Metadata.GetMetadata<SkipTenantResolution>() is null;

        if (http.User.Identity?.IsAuthenticated != true || !needsTenant ||
            http.Items[OneClaims.TokenItem] is not string token ||
            OneAuthenticationHandler.ReadIdentity(http.User) is not { } identity)
        {
            await next(http);
            return;
        }

        context.SetAccessToken(token);
        var tokenHash = OneClaims.TokenHash(token);
        var tenants = await cache.GetOrCreateAsync("one:tenants:" + tokenHash, async entry =>
        {
            var list = await identities.GetTenantsAsync(token, http.RequestAborted);
            entry.AbsoluteExpirationRelativeToNow = list is null ? TimeSpan.FromSeconds(1) : TenantsCacheFor;
            return list;
        });

        if (tenants is null)
        {
            await Problem(http, StatusCodes.Status503ServiceUnavailable, "ONE no disponible",
                "No se pudo consultar tus empresas en INTECHSYS ONE. Intenta de nuevo en un momento.", "one.unavailable");
            return;
        }

        var requested = http.Request.Headers[TenantHeader].ToString();
        OneTenantAccess? tenant;

        if (Guid.TryParse(requested, out var tenantId))
        {
            tenant = tenants.FirstOrDefault(t => t.TenantId == tenantId);
            if (tenant is null)
            {
                await Problem(http, StatusCodes.Status403Forbidden, "Acceso denegado",
                    "No tienes WorkLog en esa empresa.", "access.tenant");
                return;
            }
        }
        else if (tenants.Count == 1)
        {
            tenant = tenants[0];
        }
        else
        {
            await Problem(http, StatusCodes.Status400BadRequest, "Empresa requerida",
                tenants.Count == 0
                    ? "Tu usuario no tiene WorkLog asignado en ninguna empresa."
                    : $"Indica la empresa en la cabecera {TenantHeader}.",
                tenants.Count == 0 ? "access.no_tenants" : "access.tenant_required");
            return;
        }

        context.SetTenant(tenant.TenantId);

        var actorKey = $"wl:actor:{tokenHash}:{tenant.TenantId:N}";
        if (!cache.TryGetValue<ActorInfo>(actorKey, out var actor) || actor is null)
        {
            var resolved = await access.ResolveAsync(identity, tenant, http.RequestAborted);
            if (!resolved.Succeeded)
            {
                logger.LogInformation("Acceso negado a {Email} en {Tenant}: {Reason}", identity.Email, tenant.TenantId, resolved.Message);
                await Problem(http, StatusCodes.Status403Forbidden, "Acceso denegado", resolved.Message ?? "Acceso denegado.", resolved.Code);
                return;
            }

            actor = resolved.Value!;
            cache.Set(actorKey, actor, ActorCacheFor);
        }

        context.SetActor(actor);
        await next(http);
    }

    private static Task Problem(HttpContext http, int status, string title, string detail, string? code) =>
        Results.Problem(detail: detail, title: title, statusCode: status,
                extensions: code is null ? null : new Dictionary<string, object?> { ["code"] = code })
            .ExecuteAsync(http);
}

/// <summary>Marca endpoints autenticados que no necesitan empresa (p. ej. la lista de empresas).</summary>
public sealed class SkipTenantResolution;
