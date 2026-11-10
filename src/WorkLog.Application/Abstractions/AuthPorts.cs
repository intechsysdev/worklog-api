using WorkLog.Domain.Access;

namespace WorkLog.Application.Abstractions;

/// <summary>Hash de contraseñas con sal y costo (el de ASP.NET Core Identity). Nunca se guarda la contraseña.</summary>
public interface IPasswordHasher
{
    string Hash(string password);

    bool Verify(string hash, string password);
}

/// <summary>
/// Sesiones de la app. Las búsquedas por token cruzan empresas: al validar un token todavía no se
/// sabe de qué empresa es, y es justamente la sesión la que lo dice.
/// </summary>
public interface IMobileSessionRepository
{
    void Add(MobileSession session);

    Task<MobileSession?> FindByAccessHashAsync(string accessHash, CancellationToken ct = default);

    /// <summary>La sesión cuyo refresh vigente o anterior es <paramref name="refreshHash"/>.</summary>
    Task<MobileSession?> FindByRefreshHashAsync(string refreshHash, CancellationToken ct = default);

    /// <summary>Sesiones abiertas de una persona de la empresa actual.</summary>
    Task<IReadOnlyList<MobileSession>> ListOpenAsync(Guid memberId, CancellationToken ct = default);
}

/// <summary>Una persona que podría estar entrando a la app con ese correo, y su empresa.</summary>
public sealed record LoginCandidate(Member Member, Guid TenantId, string TenantName);

/// <summary>Personas con contraseña de la app en todas las empresas (el ingreso aún no sabe la empresa).</summary>
public interface IMobileLoginDirectory
{
    Task<IReadOnlyList<LoginCandidate>> FindByEmailAsync(string normalizedEmail, CancellationToken ct = default);

    Task<LoginCandidate?> FindAsync(Guid tenantId, Guid memberId, CancellationToken ct = default);
}
