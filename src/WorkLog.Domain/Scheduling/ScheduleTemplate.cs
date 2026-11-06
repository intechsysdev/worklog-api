using WorkLog.Domain.Common;

namespace WorkLog.Domain.Scheduling;

/// <summary>Reglas laborales con las que se valida una plantilla. Salen de los parámetros del país.</summary>
public sealed record LaborRules(
    int WeeklyCapMinutes,
    TimeOnly NightStart,
    int MinDailyMinutes = 240,
    int MaxDailyMinutes = 540,
    int MinDays = 5,
    int MaxDays = 6);

public enum CheckSeverity
{
    Pass = 1,
    Warning = 2,
    Fail = 3
}

/// <summary>Resultado de una regla sobre la plantilla, tal como se muestra en el editor.</summary>
public sealed record ScheduleCheck(string Code, CheckSeverity Severity, string Message);

/// <summary>
/// Plantilla de horario: días, entrada, salida y almuerzo. Las asignaciones la aplican a sedes,
/// grupos o personas desde una fecha.
/// </summary>
public sealed class ScheduleTemplate : TenantEntity
{
    public static readonly TimeOnly EarliestStart = new(5, 0);
    public static readonly TimeOnly LatestEnd = new(23, 0);

    private ScheduleTemplate()
    {
    }

    private ScheduleTemplate(Guid tenantId) : base(tenantId)
    {
    }

    public string Name { get; private set; } = string.Empty;
    public string NameKey { get; private set; } = string.Empty;
    public WeekDays Days { get; private set; }
    public TimeOnly StartTime { get; private set; }
    public TimeOnly EndTime { get; private set; }
    public int LunchMinutes { get; private set; }
    public bool IsActive { get; private set; } = true;

    /// <summary>Minutos trabajados al día, sin almuerzo.</summary>
    public int DailyMinutes => (int)(EndTime - StartTime).TotalMinutes - LunchMinutes;

    public int WeeklyMinutes => DailyMinutes * Days.CountDays();

    /// <summary>Resumen de una línea: "LMXJV · 07:00–16:00 · 40 h/sem".</summary>
    public string Summary =>
        $"{Days.ToLetters()} · {StartTime:HH\\:mm}–{EndTime:HH\\:mm} · {FormatHours(WeeklyMinutes)}/sem";

    public static Result<ScheduleTemplate> Create(Guid tenantId, ScheduleTemplateData data, LaborRules rules)
    {
        var template = new ScheduleTemplate(tenantId);
        var applied = template.Apply(data, rules);
        return applied.IsSuccess ? template : applied.Error!;
    }

    public Result Update(ScheduleTemplateData data, LaborRules rules) => Apply(data, rules);

    /// <summary>Una plantilla que dejó de cumplir (p. ej. bajó el tope) se desactiva; las asignaciones vigentes la conservan.</summary>
    public void Deactivate() => IsActive = false;

    public Result Activate(LaborRules rules)
    {
        if (!IsCompliant(rules))
            return DomainError.Validation("template.not_compliant", "Corrige lo que no cumple antes de activar la plantilla.");

        IsActive = true;
        return Result.Success();
    }

    public bool IsCompliant(LaborRules rules) => Evaluate(rules).All(c => c.Severity != CheckSeverity.Fail);

    public IReadOnlyList<ScheduleCheck> Evaluate(LaborRules rules) =>
        Evaluate(Days, StartTime, EndTime, LunchMinutes, rules);

    /// <summary>Reglas del editor de plantillas. Las de severidad Fail bloquean el guardado.</summary>
    public static IReadOnlyList<ScheduleCheck> Evaluate(WeekDays days, TimeOnly start, TimeOnly end, int lunchMinutes, LaborRules rules)
    {
        var checks = new List<ScheduleCheck>();
        var daily = (int)(end - start).TotalMinutes - lunchMinutes;
        var dayCount = days.CountDays();
        var weekly = daily * dayCount;

        checks.Add(daily >= rules.MinDailyMinutes && daily <= rules.MaxDailyMinutes
            ? new ScheduleCheck("daily_hours", CheckSeverity.Pass,
                $"{FormatHours(daily)} al día, entre {FormatHours(rules.MinDailyMinutes)} y {FormatHours(rules.MaxDailyMinutes)}.")
            : new ScheduleCheck("daily_hours", CheckSeverity.Fail,
                $"{FormatHours(daily)} al día: debe estar entre {FormatHours(rules.MinDailyMinutes)} y {FormatHours(rules.MaxDailyMinutes)}."));

        checks.Add(weekly <= rules.WeeklyCapMinutes
            ? new ScheduleCheck("weekly_cap", CheckSeverity.Pass,
                $"{FormatHours(weekly)} a la semana, dentro del tope de {FormatHours(rules.WeeklyCapMinutes)}.")
            : new ScheduleCheck("weekly_cap", CheckSeverity.Fail,
                $"{FormatHours(weekly)} a la semana: pasa el tope legal de {FormatHours(rules.WeeklyCapMinutes)}."));

        checks.Add(dayCount >= rules.MinDays && dayCount <= rules.MaxDays
            ? new ScheduleCheck("day_count", CheckSeverity.Pass, $"Repartidas en {dayCount} días.")
            : new ScheduleCheck("day_count", CheckSeverity.Fail,
                $"Repartidas en {dayCount} días: deben ser {rules.MinDays} o {rules.MaxDays}."));

        if ((days & WeekDays.Sunday) != 0)
        {
            checks.Add(new ScheduleCheck("sunday", CheckSeverity.Warning,
                "Incluye domingo: esas horas llevan recargo dominical."));
        }

        if (end > rules.NightStart)
        {
            checks.Add(new ScheduleCheck("night", CheckSeverity.Warning,
                $"La salida pasa de las {rules.NightStart:HH\\:mm}: ese tramo lleva recargo nocturno."));
        }

        return checks;
    }

    public static string FormatHours(int minutes)
    {
        var hours = minutes / 60;
        var rest = Math.Abs(minutes % 60);
        return rest == 0 ? $"{hours} h" : $"{hours} h {rest} m";
    }

    private Result Apply(ScheduleTemplateData data, LaborRules rules)
    {
        var name = Text.Clean(data.Name);
        if (Guard.Name(name, "name", 3, 80, "El nombre") is { } nameError) return nameError;

        if (data.Days == WeekDays.None)
            return DomainError.Validation("template.days", "Elige los días de la semana.", "days");

        if (data.StartTime < EarliestStart)
            return DomainError.Validation("template.start", "La entrada no puede ser antes de las 05:00.", "startTime");

        if (data.EndTime > LatestEnd)
            return DomainError.Validation("template.end", "La salida no puede ser después de las 23:00.", "endTime");

        if ((data.EndTime - data.StartTime).TotalMinutes < 60)
            return DomainError.Validation("template.range", "La entrada debe ser al menos una hora antes de la salida.", "endTime");

        if (Guard.Range(data.LunchMinutes, 0, 120, "lunchMinutes", "El almuerzo") is { } lunchError) return lunchError;

        var blocking = Evaluate(data.Days, data.StartTime, data.EndTime, data.LunchMinutes, rules)
            .FirstOrDefault(c => c.Severity == CheckSeverity.Fail);

        if (blocking is not null)
            return DomainError.Validation($"template.{blocking.Code}", blocking.Message);

        Name = name;
        NameKey = Text.Key(name);
        Days = data.Days;
        StartTime = data.StartTime;
        EndTime = data.EndTime;
        LunchMinutes = data.LunchMinutes;
        return Result.Success();
    }
}

public sealed record ScheduleTemplateData(string Name, WeekDays Days, TimeOnly StartTime, TimeOnly EndTime, int LunchMinutes);
