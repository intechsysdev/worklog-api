using WorkLog.Domain.Auditing;

namespace WorkLog.Application.Abstractions;

/// <summary>Confirma los cambios de un caso de uso en una sola transacción.</summary>
public interface IUnitOfWork
{
    Task SaveChangesAsync(CancellationToken ct = default);
}

/// <summary>Puerto hacia INTECHSYS ONE: identidad del usuario y empresas a las que llega con WorkLog.</summary>
public interface IIdentityProvider
{
    /// <summary>Null si ONE no reconoce el token o no respondió.</summary>
    Task<OneIdentity?> GetIdentityAsync(string accessToken, CancellationToken ct = default);

    /// <summary>Empresas con WorkLog asignado a las que llega el usuario. Null si ONE no respondió.</summary>
    Task<IReadOnlyList<OneTenantAccess>?> GetTenantsAsync(string accessToken, CancellationToken ct = default);

    /// <summary>
    /// Da de alta a una persona en la empresa de ONE (la vincula si ya tiene cuenta). Se llama con
    /// el token de quien invita: ONE decide si puede hacerlo (Owner o Admin de la empresa).
    /// </summary>
    Task<OneProvisionResult> ProvisionMemberAsync(string accessToken, Guid tenantId, string email, string firstName, string lastName, CancellationToken ct = default);
}

public sealed record OneProvisionResult(bool Succeeded, Guid? UserId, bool UserCreated, string? TemporaryPassword, string? Error, bool Forbidden)
{
    public static OneProvisionResult Ok(Guid userId, bool created, string? temporaryPassword) => new(true, userId, created, temporaryPassword, null, false);

    public static OneProvisionResult Fail(string error, bool forbidden = false) => new(false, null, false, null, error, forbidden);
}

/// <summary>Credencial con que llegó la petición, para llamar a ONE en nombre de quien la hace.</summary>
public interface ICallerCredentials
{
    string? AccessToken { get; }
}

/// <summary>Almacenamiento de archivos (evidencias, soportes). En producción, Azure Blob Storage.</summary>
public interface IFileStorage
{
    Task SaveAsync(string key, Stream content, string contentType, CancellationToken ct = default);

    Task<StoredFile?> OpenAsync(string key, CancellationToken ct = default);
}

public sealed record StoredFile(Stream Content, string ContentType, long Length);

/// <summary>Registra eventos de auditoría con el actor de la petición. Se confirman con el caso de uso.</summary>
public interface IAuditTrail
{
    void Record(string action, string summary, string entityType, string? entityId,
        object? before = null, object? after = null, DateTimeOffset? occurredAt = null);

    /// <summary>Evento del sistema (procesos automáticos), sin persona.</summary>
    void RecordSystem(Guid tenantId, string action, string summary, string entityType, string? entityId,
        object? before = null, object? after = null);
}

/// <summary>Escritura de eventos de auditoría (solo agregar).</summary>
public interface IAuditStore
{
    void Add(AuditEvent auditEvent);
}
