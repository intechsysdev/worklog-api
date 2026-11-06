using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using WorkLog.Application.Abstractions;

namespace WorkLog.Api.Auth;

/// <summary>Claims propios que WorkLog arma a partir de la identidad de ONE.</summary>
public static class OneClaims
{
    public const string Scheme = "One";
    public const string PlatformAdmin = "one:platform_admin";

    /// <summary>Clave en HttpContext.Items con el token, para consultar a ONE las empresas del usuario.</summary>
    public const string TokenItem = "one:token";

    public static string TokenHash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}

/// <summary>
/// Valida el Bearer preguntándole a ONE (/auth/me) y cachea el resultado unos segundos. La caché
/// se indexa por el resumen del token, nunca por el token. ONE rechaza los tokens de sesiones
/// cerradas, así que la caché corta es lo único que demora un cierre de sesión aquí.
/// </summary>
public sealed class OneAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IIdentityProvider identities,
    IMemoryCache cache) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(15);

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return AuthenticateResult.NoResult();

        var token = header[7..].Trim();
        if (token.Length == 0) return AuthenticateResult.NoResult();

        var key = "one:me:" + OneClaims.TokenHash(token);
        if (!cache.TryGetValue<OneIdentity>(key, out var identity) || identity is null)
        {
            identity = await identities.GetIdentityAsync(token, Context.RequestAborted);
            if (identity is null) return AuthenticateResult.Fail("ONE no reconoce el token.");
            cache.Set(key, identity, CacheFor);
        }

        Context.Items[OneClaims.TokenItem] = token;

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, identity.UserId.ToString()),
            new(ClaimTypes.Email, identity.Email),
            new(ClaimTypes.Name, identity.FullName)
        };
        if (identity.IsPlatformAdmin) claims.Add(new Claim(OneClaims.PlatformAdmin, "true"));

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, OneClaims.Scheme));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, OneClaims.Scheme));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer";
        return Task.CompletedTask;
    }

    public static OneIdentity? ReadIdentity(ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
            ? new OneIdentity(id, user.FindFirstValue(ClaimTypes.Email) ?? string.Empty,
                user.FindFirstValue(ClaimTypes.Name) ?? string.Empty, user.HasClaim(OneClaims.PlatformAdmin, "true"))
            : null;
}
