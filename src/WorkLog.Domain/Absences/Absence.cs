using WorkLog.Domain.Common;

namespace WorkLog.Domain.Absences;

public enum AbsenceType
{
    Vacation = 1,
    SickLeave = 2,
    Leave = 3,
    Permission = 4,
    CompensatoryTime = 5
}

public enum AbsenceStatus
{
    Pending = 1,
    Approved = 2,
    Rejected = 3
}

/// <summary>
/// Novedad: mientras dure, la persona no tiene turno, la app no deja iniciar jornada y no se
/// registra ubicación. Un permiso puede ser por horas dentro de un solo día.
/// </summary>
public sealed class Absence : TenantEntity
{
    private Absence()
    {
    }

    private Absence(Guid tenantId) : base(tenantId)
    {
    }

    public Guid MemberId { get; private set; }
    public AbsenceType Type { get; private set; }
    public DateOnly From { get; private set; }
    public DateOnly To { get; private set; }
    public TimeOnly? FromTime { get; private set; }
    public TimeOnly? ToTime { get; private set; }
    public string? Notes { get; private set; }
    public string? SupportFileKey { get; private set; }
    public AbsenceStatus Status { get; private set; }
    public Guid? ResolvedBy { get; private set; }
    public DateTimeOffset? ResolvedAt { get; private set; }

    /// <summary>Por horas: no quita el turno completo del día.</summary>
    public bool IsPartialDay => FromTime is not null;

    public bool Covers(DateOnly date) => Status == AbsenceStatus.Approved && date >= From && date <= To;

    public static Result<Absence> Register(Guid tenantId, Guid memberId, AbsenceType type, DateOnly from, DateOnly to, TimeOnly? fromTime, TimeOnly? toTime, string? notes)
    {
        if (memberId == Guid.Empty)
            return DomainError.Validation("absence.member", "Elige el usuario.", "memberId");

        if (to < from)
            return DomainError.Validation("absence.range", "La fecha final debe ser igual o posterior a la inicial.", "to");

        if (fromTime is not null || toTime is not null)
        {
            if (type != AbsenceType.Permission || from != to)
                return DomainError.Validation("absence.hours", "Solo un permiso de un día puede ser por horas.", "fromTime");

            if (fromTime is null || toTime is null || toTime <= fromTime)
                return DomainError.Validation("absence.hours_range", "La hora final debe ser posterior a la inicial.", "toTime");
        }

        var cleaned = Text.CleanOrNull(notes);
        if (Guard.MaxLength(cleaned, "notes", 500, "La nota") is { } error) return error;

        return new Absence(tenantId)
        {
            MemberId = memberId,
            Type = type,
            From = from,
            To = to,
            FromTime = fromTime,
            ToTime = toTime,
            Notes = cleaned,
            Status = AbsenceStatus.Pending
        };
    }

    public void AttachSupport(string fileKey) => SupportFileKey = fileKey;

    public Result Approve(Guid approverId, DateTimeOffset at)
    {
        if (Status != AbsenceStatus.Pending)
            return DomainError.Conflict("absence.resolved", "La novedad ya fue resuelta.");

        Status = AbsenceStatus.Approved;
        ResolvedBy = approverId;
        ResolvedAt = at;
        return Result.Success();
    }

    public Result Reject(Guid approverId, DateTimeOffset at)
    {
        if (Status != AbsenceStatus.Pending)
            return DomainError.Conflict("absence.resolved", "La novedad ya fue resuelta.");

        Status = AbsenceStatus.Rejected;
        ResolvedBy = approverId;
        ResolvedAt = at;
        return Result.Success();
    }
}
