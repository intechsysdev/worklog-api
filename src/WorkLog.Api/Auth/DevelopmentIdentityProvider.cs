using System.Security.Cryptography;
using System.Text;
using WorkLog.Application.Abstractions;

namespace WorkLog.Api.Auth;

/// <summary>
/// Identidad de desarrollo para probar sin INTECHSYS ONE: el token es "dev:correo". Solo se
/// registra con Auth:Mode = Development y en el entorno Development (el arranque falla si no).
/// Los correos de Auth:Development:Admins entran como Owner de la empresa de prueba.
/// </summary>
public sealed class DevelopmentIdentityProvider(IConfiguration configuration) : IIdentityProvider
{
    public const string Prefix = "dev:";

    private readonly Guid _tenantId = Guid.TryParse(configuration["Auth:Development:TenantId"], out var id)
        ? id
        : Guid.Parse("0199f0a0-0000-7000-8000-000000000001");

    private readonly string _tenantName = configuration["Auth:Development:TenantName"] ?? "INTECHSYS (desarrollo)";

    private readonly HashSet<string> _admins = new(
        configuration.GetSection("Auth:Development:Admins").Get<string[]>() ?? [], StringComparer.OrdinalIgnoreCase);

    public Task<OneIdentity?> GetIdentityAsync(string accessToken, CancellationToken ct = default)
    {
        if (Email(accessToken) is not { } email) return Task.FromResult<OneIdentity?>(null);

        // Sin nombre: al ligar una invitación se conserva el nombre con que se invitó.
        return Task.FromResult<OneIdentity?>(new OneIdentity(StableId(email), email, string.Empty, false));
    }

    public Task<IReadOnlyList<OneTenantAccess>?> GetTenantsAsync(string accessToken, CancellationToken ct = default)
    {
        if (Email(accessToken) is not { } email) return Task.FromResult<IReadOnlyList<OneTenantAccess>?>(null);

        IReadOnlyList<OneTenantAccess> tenants = [new(_tenantId, _tenantName, "intechsys-dev", _admins.Contains(email) ? "Owner" : "Member")];
        return Task.FromResult<IReadOnlyList<OneTenantAccess>?>(tenants);
    }

    public Task<OneProvisionResult> ProvisionMemberAsync(string accessToken, Guid tenantId, string email, string firstName,
        string lastName, CancellationToken ct = default) =>
        Task.FromResult(OneProvisionResult.Ok(StableId(email.Trim().ToLowerInvariant()), created: false, temporaryPassword: null));

    private static string? Email(string token) =>
        token.StartsWith(Prefix, StringComparison.Ordinal) && token.Contains('@', StringComparison.Ordinal)
            ? token[Prefix.Length..].Trim().ToLowerInvariant()
            : null;

    /// <summary>Mismo correo, mismo id: así la persona de WorkLog se conserva entre reinicios.</summary>
    private static Guid StableId(string email)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("worklog-dev:" + email));
        return new Guid(hash.AsSpan(0, 16));
    }
}
