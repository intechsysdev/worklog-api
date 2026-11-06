using WorkLog.Domain.Common;

namespace WorkLog.Domain.Sync;

public enum SyncOutcome
{
    Accepted = 1,

    /// <summary>Se aceptó con un valor corregido por el servidor (p. ej. hora fuera de la ventana).</summary>
    Adjusted = 2,

    Rejected = 3
}

/// <summary>
/// Evento del teléfono ya procesado. El teléfono reintenta lo que no sabe si llegó; con esto
/// cada evento produce efecto una sola vez y el reintento recibe la misma respuesta.
/// </summary>
public sealed class ProcessedClientEvent : Entity, ITenantOwned
{
    private ProcessedClientEvent()
    {
    }

    public Guid TenantId { get; private set; }
    public Guid MemberId { get; private set; }
    public Guid ClientEventId { get; private set; }
    public string Kind { get; private set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; private set; }
    public DateTimeOffset ProcessedAt { get; private set; }
    public SyncOutcome Outcome { get; private set; }
    public string? Message { get; private set; }

    /// <summary>Id del registro que creó el evento (jornada, actividad…), para devolverlo en reintentos.</summary>
    public Guid? ResultId { get; private set; }

    public static ProcessedClientEvent Create(Guid tenantId, Guid memberId, Guid clientEventId, string kind,
        DateTimeOffset occurredAt, DateTimeOffset processedAt, SyncOutcome outcome, string? message, Guid? resultId) => new()
    {
        TenantId = tenantId,
        MemberId = memberId,
        ClientEventId = clientEventId,
        Kind = kind,
        OccurredAt = occurredAt,
        ProcessedAt = processedAt,
        Outcome = outcome,
        Message = message is { Length: > 400 } ? message[..400] : message,
        ResultId = resultId
    };
}
