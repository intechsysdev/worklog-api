using WorkLog.Api.Extensions;
using WorkLog.Application.Access;

namespace WorkLog.Api.Endpoints;

/// <summary>
/// Ingreso a la app con la contraseña de WorkLog. Son anónimos; el ingreso lleva un límite por IP
/// (<see cref="AuthRateLimit"/>) además del bloqueo por persona tras varios intentos fallidos. Renovar
/// y salir no: los teléfonos de una misma red (misma IP) renuevan solos y no deben chocar con ese límite.
/// </summary>
public static class MobileAuthEndpoints
{
    public const string AuthRateLimit = "auth";

    public static IEndpointRouteBuilder MapMobileAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/v1/mobile-auth").WithTags("Ingreso a la app").AllowAnonymous();

        auth.MapPost("/login", async (MobileLoginCommand command, IMobileAuthService service, CancellationToken ct) =>
                (await service.LoginAsync(command, ct)).ToHttpResult())
            .RequireRateLimiting(AuthRateLimit)
            .Produces<MobileLoginResult>()
            .WithSummary("Correo y contraseña de WorkLog. Si sirven en varias empresas responde la lista: se elige y se repite con tenantId.");

        auth.MapPost("/refresh", async (RefreshSessionCommand command, IMobileAuthService service, CancellationToken ct) =>
                (await service.RefreshAsync(command, ct)).ToHttpResult())
            .Produces<MobileSessionDto>()
            .WithSummary("Renueva la sesión: entrega tokens nuevos y el refresh anterior deja de servir.");

        auth.MapPost("/logout", async (RefreshSessionCommand command, IMobileAuthService service, CancellationToken ct) =>
                (await service.LogoutAsync(command, ct)).ToHttpResult())
            .WithSummary("Cierra la sesión del teléfono.");

        return app;
    }
}
