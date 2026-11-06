using WorkLog.Domain.Absences;

namespace WorkLog.Domain.Scheduling;

/// <summary>Qué tiene una persona un día concreto.</summary>
public enum DayKind
{
    /// <summary>Turno según su plantilla.</summary>
    Shift = 1,

    /// <summary>Novedad aprobada (vacaciones, incapacidad…): no tiene turno.</summary>
    Absence = 2,

    /// <summary>Cambio puntual aprobado.</summary>
    Override = 3,

    Holiday = 4,

    /// <summary>Día que la plantilla no incluye.</summary>
    Rest = 5,

    /// <summary>No hay asignación que la cubra.</summary>
    Unscheduled = 6
}

/// <summary>Plan del día: turno, origen y etiqueta para mostrar.</summary>
public sealed record DayPlan(
    DateOnly Date,
    DayKind Kind,
    TimeOnly? Start,
    TimeOnly? End,
    int LunchMinutes,
    Guid? TemplateId,
    string? TemplateName,
    AssignmentLevel? Source,
    Guid? SourceTargetId,
    string? Label)
{
    /// <summary>Con horario de trabajo: turno normal o cambio puntual con horas.</summary>
    public bool IsWorkingDay => Start is not null && End is not null;

    public int ScheduledMinutes => IsWorkingDay ? (int)(End!.Value - Start!.Value).TotalMinutes - LunchMinutes : 0;
}

/// <summary>Lo que hace falta saber de una persona para calcular su turno.</summary>
public sealed record ScheduleContext(
    Guid MemberId,
    Guid? GroupId,
    Guid? BranchId,
    IReadOnlyList<ScheduleAssignment> Assignments,
    IReadOnlyDictionary<Guid, ScheduleTemplate> Templates,
    IReadOnlyList<ShiftOverride> Overrides,
    IReadOnlyList<Absence> Absences,
    IReadOnlyList<Holiday> Holidays);

/// <summary>
/// Calcula el turno efectivo de un día. Precedencia: novedad aprobada → cambio puntual →
/// festivo → asignación a la persona → al grupo → a la sede. Dentro de un nivel gana la
/// asignación con la fecha "desde" más reciente que ya esté vigente.
/// </summary>
public static class ShiftResolver
{
    public static DayPlan Resolve(DateOnly date, ScheduleContext context)
    {
        var baseline = ResolveBaseline(date, context);

        if (context.Absences.FirstOrDefault(a => a.MemberId == context.MemberId && a.Covers(date) && !a.IsPartialDay) is { } absence &&
            baseline.Kind is DayKind.Shift)
        {
            return baseline with { Kind = DayKind.Absence, Start = null, End = null, LunchMinutes = 0, Label = AbsenceLabel(absence.Type) };
        }

        if (context.Overrides.FirstOrDefault(o => o.MemberId == context.MemberId && o.Date == date) is { } change)
        {
            return change.IsDayOff
                ? baseline with { Kind = DayKind.Override, Start = null, End = null, LunchMinutes = 0, Label = "Cambio puntual · día libre" }
                : baseline with
                {
                    Kind = DayKind.Override,
                    Start = change.StartTime,
                    End = change.EndTime,
                    LunchMinutes = change.LunchMinutes,
                    Label = "Cambio puntual"
                };
        }

        return baseline;
    }

    public static IReadOnlyList<DayPlan> ResolveRange(DateOnly from, DateOnly to, ScheduleContext context)
    {
        var days = new List<DayPlan>();
        for (var day = from; day <= to; day = day.AddDays(1))
            days.Add(Resolve(day, context));
        return days;
    }

    private static DayPlan ResolveBaseline(DateOnly date, ScheduleContext context)
    {
        var assignment = Pick(date, context, AssignmentLevel.Member, context.MemberId)
                         ?? Pick(date, context, AssignmentLevel.Group, context.GroupId)
                         ?? Pick(date, context, AssignmentLevel.Branch, context.BranchId);

        if (assignment is null || !context.Templates.TryGetValue(assignment.TemplateId, out var template))
            return new DayPlan(date, DayKind.Unscheduled, null, null, 0, null, null, null, null, "Sin horario");

        var plan = new DayPlan(date, DayKind.Shift, template.StartTime, template.EndTime, template.LunchMinutes,
            template.Id, template.Name, assignment.Level, assignment.TargetId, template.Name);

        if (context.Holidays.FirstOrDefault(h => h.Date == date) is { } holiday)
            return plan with { Kind = DayKind.Holiday, Start = null, End = null, LunchMinutes = 0, Label = holiday.Name };

        if (!template.Days.Includes(date.DayOfWeek))
            return plan with { Kind = DayKind.Rest, Start = null, End = null, LunchMinutes = 0, Label = "Descanso" };

        return plan;
    }

    private static ScheduleAssignment? Pick(DateOnly date, ScheduleContext context, AssignmentLevel level, Guid? targetId) =>
        targetId is null
            ? null
            : context.Assignments
                .Where(a => a.Level == level && a.TargetId == targetId && a.EffectiveFrom <= date)
                .MaxBy(a => a.EffectiveFrom);

    public static string AbsenceLabel(AbsenceType type) => type switch
    {
        AbsenceType.Vacation => "Vacaciones",
        AbsenceType.SickLeave => "Incapacidad",
        AbsenceType.Leave => "Licencia",
        AbsenceType.Permission => "Permiso",
        _ => "Compensatorio"
    };
}
