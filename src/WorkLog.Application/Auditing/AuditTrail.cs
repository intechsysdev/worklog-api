using System.Text.Json;
using WorkLog.Application.Abstractions;
using WorkLog.Domain.Auditing;

namespace WorkLog.Application.Auditing;

/// <summary>
/// Arma el evento de auditoría con el actor de la petición. Solo agrega a la unidad de trabajo:
/// se guarda en la misma transacción que el cambio que describe, ni antes ni sin él.
/// </summary>
internal sealed class AuditTrail(ICurrentActor actor, IAuditStore store, TimeProvider clock) : IAuditTrail
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public void Record(string action, string summary, string entityType, string? entityId,
        object? before = null, object? after = null, DateTimeOffset? occurredAt = null)
    {
        var now = clock.GetUtcNow();
        store.Add(AuditEvent.Create(new AuditEntry(
            actor.TenantId,
            occurredAt ?? now,
            occurredAt is null ? null : now,
            actor.MemberId,
            actor.Name,
            actor.RoleName,
            actor.Origin,
            action,
            summary,
            entityType,
            entityId,
            Serialize(before),
            Serialize(after))));
    }

    public void RecordSystem(Guid tenantId, string action, string summary, string entityType, string? entityId,
        object? before = null, object? after = null) =>
        store.Add(AuditEvent.Create(new AuditEntry(
            tenantId,
            clock.GetUtcNow(),
            null,
            null,
            "Sistema",
            null,
            AuditOrigin.System,
            action,
            summary,
            entityType,
            entityId,
            Serialize(before),
            Serialize(after))));

    private static string? Serialize(object? value) => value switch
    {
        null => null,
        string text => text,
        _ => JsonSerializer.Serialize(value, Json)
    };
}
