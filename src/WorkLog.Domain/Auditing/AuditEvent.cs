using WorkLog.Domain.Common;

namespace WorkLog.Domain.Auditing;

/// <summary>Desde dónde se originó el cambio.</summary>
public enum AuditOrigin
{
    Web = 1,
    Mobile = 2,
    System = 3
}

/// <summary>
/// Registro de auditoría. Es de solo lectura: no expone métodos para cambiarlo y la persistencia
/// no ofrece actualizar ni borrar, ni siquiera para un administrador.
/// </summary>
public sealed class AuditEvent : Entity, ITenantOwned
{
    private AuditEvent()
    {
    }

    public Guid TenantId { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; }

    /// <summary>Hora de llegada al servidor, cuando difiere (eventos que venían de la cola sin conexión).</summary>
    public DateTimeOffset? ReceivedAt { get; private set; }

    public Guid? ActorMemberId { get; private set; }
    public string ActorName { get; private set; } = string.Empty;
    public string? ActorRole { get; private set; }
    public AuditOrigin Origin { get; private set; }

    /// <summary>Código estable de la acción, p. ej. "workday.started".</summary>
    public string Action { get; private set; } = string.Empty;

    /// <summary>Qué pasó, en una línea legible.</summary>
    public string Summary { get; private set; } = string.Empty;

    public string EntityType { get; private set; } = string.Empty;
    public string? EntityId { get; private set; }
    public string? Before { get; private set; }
    public string? After { get; private set; }

    public static AuditEvent Create(AuditEntry entry) => new()
    {
        TenantId = entry.TenantId,
        OccurredAt = entry.OccurredAt,
        ReceivedAt = entry.ReceivedAt is { } received && received - entry.OccurredAt > TimeSpan.FromMinutes(1) ? received : null,
        ActorMemberId = entry.ActorMemberId,
        ActorName = Truncate(entry.ActorName, 150),
        ActorRole = entry.ActorRole is null ? null : Truncate(entry.ActorRole, 80),
        Origin = entry.Origin,
        Action = Truncate(entry.Action, 80),
        Summary = Truncate(entry.Summary, 400),
        EntityType = Truncate(entry.EntityType, 60),
        EntityId = entry.EntityId,
        Before = entry.Before,
        After = entry.After
    };

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}

public sealed record AuditEntry(
    Guid TenantId,
    DateTimeOffset OccurredAt,
    DateTimeOffset? ReceivedAt,
    Guid? ActorMemberId,
    string ActorName,
    string? ActorRole,
    AuditOrigin Origin,
    string Action,
    string Summary,
    string EntityType,
    string? EntityId,
    string? Before = null,
    string? After = null);
