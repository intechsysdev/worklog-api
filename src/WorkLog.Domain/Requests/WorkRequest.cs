using WorkLog.Domain.Common;

namespace WorkLog.Domain.Requests;

public enum RequestType
{
    /// <summary>Corregir una hora mal registrada (inicio o fin de actividad o de jornada).</summary>
    RecordAdjustment = 1,

    /// <summary>Autorización de horas extra para un día.</summary>
    Overtime = 2,

    /// <summary>Cambiar el turno de uno o varios días.</summary>
    ShiftChange = 3
}

public enum RequestStatus
{
    Pending = 1,
    Approved = 2,
    Rejected = 3,
    Cancelled = 4
}

/// <summary>Qué hora se corrige en un ajuste.</summary>
public enum AdjustmentTarget
{
    WorkdayStart = 1,
    WorkdayEnd = 2,
    ActivityStart = 3,
    ActivityEnd = 4
}

/// <summary>Un día propuesto en un cambio de turno. Sin horas significa día libre.</summary>
public sealed record ShiftChangeDay(DateOnly Date, TimeOnly? Start, TimeOnly? End, int LunchMinutes);

/// <summary>
/// Solicitud que el usuario envía desde la app y aprueba su supervisor. Al aprobarse produce su
/// efecto (corrige la jornada, autoriza horas extra o crea cambios puntuales de turno).
/// </summary>
public sealed class WorkRequest : TenantEntity
{
    private readonly List<ShiftChangeDay> _days = [];

    private WorkRequest()
    {
    }

    private WorkRequest(Guid tenantId) : base(tenantId)
    {
    }

    public Guid MemberId { get; private set; }
    public RequestType Type { get; private set; }
    public RequestStatus Status { get; private set; }
    public string Reason { get; private set; } = string.Empty;
    public DateTimeOffset SubmittedAt { get; private set; }

    /// <summary>Día al que se refiere (el del registro, las horas extra o el primer día del cambio).</summary>
    public DateOnly Date { get; private set; }

    // Ajuste de registro
    public Guid? WorkdayId { get; private set; }
    public Guid? ActivityRecordId { get; private set; }
    public AdjustmentTarget? Target { get; private set; }
    public DateTimeOffset? OriginalValue { get; private set; }
    public DateTimeOffset? ProposedValue { get; private set; }

    // Horas extra
    public TimeOnly? OvertimeFrom { get; private set; }
    public TimeOnly? OvertimeTo { get; private set; }
    public string? AuthorizationReference { get; private set; }

    // Cambio de turno
    public IReadOnlyList<ShiftChangeDay> Days => _days;

    public Guid? ResolvedBy { get; private set; }
    public DateTimeOffset? ResolvedAt { get; private set; }
    public string? ResolverComment { get; private set; }

    public bool IsPending => Status == RequestStatus.Pending;

    public int OvertimeMinutes => OvertimeFrom is { } from && OvertimeTo is { } to ? (int)(to - from).TotalMinutes : 0;

    public static Result<WorkRequest> Adjustment(Guid tenantId, Guid memberId, DateOnly date, Guid workdayId, Guid? activityId,
        AdjustmentTarget target, DateTimeOffset? original, DateTimeOffset proposed, string? reason, DateTimeOffset at)
    {
        if (target is AdjustmentTarget.ActivityStart or AdjustmentTarget.ActivityEnd && activityId is null)
            return DomainError.Validation("request.activity", "Elige la actividad a corregir.", "activityRecordId");

        if (original is { } before && before == proposed)
            return DomainError.Validation("request.same_value", "La hora propuesta es igual a la registrada.", "proposedValue");

        return Create(tenantId, memberId, RequestType.RecordAdjustment, date, reason, at, r =>
        {
            r.WorkdayId = workdayId;
            r.ActivityRecordId = activityId;
            r.Target = target;
            r.OriginalValue = original;
            r.ProposedValue = proposed;
        });
    }

    public static Result<WorkRequest> OvertimeRequest(Guid tenantId, Guid memberId, DateOnly date, TimeOnly from, TimeOnly to,
        string? authorizationReference, string? reason, DateTimeOffset at)
    {
        if (to <= from)
            return DomainError.Validation("request.overtime_range", "La hora final debe ser posterior a la inicial.", "overtimeTo");

        var reference = Text.CleanOrNull(authorizationReference);
        if (Guard.MaxLength(reference, "authorizationReference", 120, "La autorización") is { } error) return error;

        return Create(tenantId, memberId, RequestType.Overtime, date, reason, at, r =>
        {
            r.OvertimeFrom = from;
            r.OvertimeTo = to;
            r.AuthorizationReference = reference;
        });
    }

    public static Result<WorkRequest> ShiftChangeRequest(Guid tenantId, Guid memberId, IReadOnlyList<ShiftChangeDay> days, string? reason, DateTimeOffset at)
    {
        if (days.Count is 0 or > 7)
            return DomainError.Validation("request.days", "Propón entre 1 y 7 días.", "days");

        if (days.Select(d => d.Date).Distinct().Count() != days.Count)
            return DomainError.Validation("request.days_repeated", "Hay días repetidos.", "days");

        foreach (var day in days)
        {
            if (day.Start is not null && (day.End is null || day.End <= day.Start))
                return DomainError.Validation("request.day_range", $"El {day.Date:dd/MM}: la salida debe ser después de la entrada.", "days");
        }

        return Create(tenantId, memberId, RequestType.ShiftChange, days.Min(d => d.Date), reason, at,
            r => r._days.AddRange(days.OrderBy(d => d.Date)));
    }

    public Result Approve(Guid approverId, string? comment, DateTimeOffset at)
    {
        if (!IsPending) return DomainError.Conflict("request.resolved", "La solicitud ya fue resuelta.");
        if (approverId == MemberId) return DomainError.Forbidden("request.self", "No puedes aprobar tus propias solicitudes.");

        Status = RequestStatus.Approved;
        Resolve(approverId, comment, at);
        return Result.Success();
    }

    public Result Reject(Guid approverId, string? comment, DateTimeOffset at)
    {
        if (!IsPending) return DomainError.Conflict("request.resolved", "La solicitud ya fue resuelta.");
        if (approverId == MemberId) return DomainError.Forbidden("request.self", "No puedes resolver tus propias solicitudes.");

        if (Text.CleanOrNull(comment) is not { Length: >= 5 })
            return DomainError.Validation("request.comment", "Escribe el motivo del rechazo para el usuario.", "comment");

        Status = RequestStatus.Rejected;
        Resolve(approverId, comment, at);
        return Result.Success();
    }

    public Result Cancel(Guid memberId, DateTimeOffset at)
    {
        if (memberId != MemberId) return DomainError.Forbidden("request.not_owner", "Solo quien la envió puede cancelarla.");
        if (!IsPending) return DomainError.Conflict("request.resolved", "La solicitud ya fue resuelta.");

        Status = RequestStatus.Cancelled;
        ResolvedAt = at;
        return Result.Success();
    }

    private void Resolve(Guid approverId, string? comment, DateTimeOffset at)
    {
        ResolvedBy = approverId;
        ResolvedAt = at;
        ResolverComment = Text.CleanOrNull(comment) is { } c && c.Length > 500 ? c[..500] : Text.CleanOrNull(comment);
    }

    private static Result<WorkRequest> Create(Guid tenantId, Guid memberId, RequestType type, DateOnly date, string? reason,
        DateTimeOffset at, Action<WorkRequest> fill)
    {
        var cleaned = Text.Clean(reason);
        if (Guard.Name(cleaned, "reason", 5, 500, "El motivo") is { } error) return error;

        var request = new WorkRequest(tenantId)
        {
            MemberId = memberId,
            Type = type,
            Status = RequestStatus.Pending,
            Reason = cleaned,
            SubmittedAt = at,
            Date = date
        };
        fill(request);
        return request;
    }
}
