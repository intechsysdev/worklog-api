using WorkLog.Domain.Common;

namespace WorkLog.Domain.Scheduling;

/// <summary>A quién aplica la asignación. Gana la más específica: persona, luego grupo, luego sede.</summary>
public enum AssignmentLevel
{
    Branch = 1,
    Group = 2,
    Member = 3
}

/// <summary>Aplica una plantilla a una sede, grupo o persona desde una fecha (sin fecha fin: la reemplaza otra posterior).</summary>
public sealed class ScheduleAssignment : TenantEntity
{
    private ScheduleAssignment()
    {
    }

    private ScheduleAssignment(Guid tenantId) : base(tenantId)
    {
    }

    public AssignmentLevel Level { get; private set; }
    public Guid TargetId { get; private set; }
    public Guid TemplateId { get; private set; }
    public DateOnly EffectiveFrom { get; private set; }

    public static Result<ScheduleAssignment> Create(Guid tenantId, AssignmentLevel level, Guid targetId, Guid templateId, DateOnly effectiveFrom)
    {
        if (targetId == Guid.Empty)
        {
            var message = level switch
            {
                AssignmentLevel.Member => "Elige la persona.",
                AssignmentLevel.Group => "Elige el grupo.",
                _ => "Elige la sede."
            };
            return DomainError.Validation("assignment.target", message, "targetId");
        }

        if (templateId == Guid.Empty)
            return DomainError.Validation("assignment.template", "Elige el horario.", "templateId");

        if (effectiveFrom == default)
            return DomainError.Validation("assignment.from", "Elige la fecha de inicio.", "effectiveFrom");

        return new ScheduleAssignment(tenantId)
        {
            Level = level,
            TargetId = targetId,
            TemplateId = templateId,
            EffectiveFrom = effectiveFrom
        };
    }
}

/// <summary>
/// Cambio puntual del turno de un día para una persona (sale de una solicitud aprobada).
/// Sin hora de inicio significa día libre.
/// </summary>
public sealed class ShiftOverride : TenantEntity
{
    private ShiftOverride()
    {
    }

    private ShiftOverride(Guid tenantId) : base(tenantId)
    {
    }

    public Guid MemberId { get; private set; }
    public DateOnly Date { get; private set; }
    public TimeOnly? StartTime { get; private set; }
    public TimeOnly? EndTime { get; private set; }
    public int LunchMinutes { get; private set; }
    public string Reason { get; private set; } = string.Empty;
    public Guid? SourceRequestId { get; private set; }

    public bool IsDayOff => StartTime is null;

    public static Result<ShiftOverride> Create(Guid tenantId, Guid memberId, DateOnly date, TimeOnly? start, TimeOnly? end, int lunchMinutes, string reason, Guid? sourceRequestId)
    {
        if (start is not null && (end is null || end <= start))
            return DomainError.Validation("override.range", "La salida debe ser después de la entrada.", "endTime");

        var cleaned = Text.Clean(reason);
        if (Guard.Name(cleaned, "reason", 3, 300, "El motivo") is { } error) return error;

        return new ShiftOverride(tenantId)
        {
            MemberId = memberId,
            Date = date,
            StartTime = start,
            EndTime = start is null ? null : end,
            LunchMinutes = Math.Clamp(lunchMinutes, 0, 120),
            Reason = cleaned,
            SourceRequestId = sourceRequestId
        };
    }
}

/// <summary>Festivo de un país. Es dato del producto: lo comparten todas las empresas.</summary>
public sealed class Holiday : Entity
{
    private Holiday()
    {
    }

    public string CountryCode { get; private set; } = string.Empty;
    public DateOnly Date { get; private set; }
    public string Name { get; private set; } = string.Empty;

    public static Holiday Create(string countryCode, DateOnly date, string name) => new()
    {
        CountryCode = countryCode.ToUpperInvariant(),
        Date = date,
        Name = Text.Clean(name)
    };
}
