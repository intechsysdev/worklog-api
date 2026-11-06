namespace WorkLog.Domain.Hours;

/// <summary>Reglas del cálculo: tope semanal y franja nocturna (de los parámetros legales del país).</summary>
public sealed record HourRules(int WeeklyCapMinutes, TimeOnly NightStart, TimeOnly NightEnd)
{
    public bool IsNight(TimeOnly time) =>
        NightStart > NightEnd
            ? time >= NightStart || time < NightEnd
            : time >= NightStart && time < NightEnd;
}

/// <summary>Minutos por categoría. Dominical/festivo se superpone a las demás (es un recargo, no otra hora).</summary>
public sealed record HoursBreakdown(int OrdinaryDay, int OrdinaryNight, int OvertimeDay, int OvertimeNight, int SundayHoliday)
{
    public static HoursBreakdown Zero { get; } = new(0, 0, 0, 0, 0);

    public int Ordinary => OrdinaryDay + OrdinaryNight;
    public int Overtime => OvertimeDay + OvertimeNight;
    public int Total => Ordinary + Overtime;

    public HoursBreakdown Add(HoursBreakdown other) => new(
        OrdinaryDay + other.OrdinaryDay,
        OrdinaryNight + other.OrdinaryNight,
        OvertimeDay + other.OvertimeDay,
        OvertimeNight + other.OvertimeNight,
        SundayHoliday + other.SundayHoliday);
}

/// <summary>
/// Trabajo de un día: tramos pagados y minutos programados. <see cref="ScheduledMinutes"/> null
/// significa que no hay horario asignado: no se aplica tope diario, solo el semanal.
/// </summary>
public sealed record DayWork(DateOnly Date, IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> Intervals, int? ScheduledMinutes);

public sealed record DayHours(DateOnly Date, int WorkedMinutes, int? ScheduledMinutes, HoursBreakdown Breakdown);

/// <summary>
/// Clasifica el tiempo trabajado minuto a minuto. Un minuto es ordinario mientras no pase lo
/// programado para ese día ni el tope de la semana (lunes a domingo); lo demás es extra. Cada
/// minuto es nocturno o diurno según la hora local, y cuenta como dominical/festivo si cae en
/// domingo o en un festivo del país.
/// </summary>
public static class HoursCalculator
{
    public static (IReadOnlyList<DayHours> Days, HoursBreakdown Total) Calculate(
        IEnumerable<DayWork> work, TimeZoneInfo zone, HourRules rules, IReadOnlySet<DateOnly> holidays)
    {
        var days = new List<DayHours>();
        var total = HoursBreakdown.Zero;
        var weekOrdinary = 0;
        DateOnly? currentWeek = null;

        foreach (var day in work.OrderBy(d => d.Date))
        {
            var week = StartOfWeek(day.Date);
            if (week != currentWeek)
            {
                currentWeek = week;
                weekOrdinary = 0;
            }

            int ordinaryDay = 0, ordinaryNight = 0, overtimeDay = 0, overtimeNight = 0, sundayHoliday = 0, dayOrdinary = 0;

            foreach (var (start, end) in day.Intervals.OrderBy(i => i.Start))
            {
                for (var minute = start; minute.AddMinutes(1) <= end; minute = minute.AddMinutes(1))
                {
                    var local = TimeZoneInfo.ConvertTime(minute, zone);
                    var localDate = DateOnly.FromDateTime(local.DateTime);
                    var night = rules.IsNight(TimeOnly.FromDateTime(local.DateTime));

                    var ordinary = (day.ScheduledMinutes is null || dayOrdinary < day.ScheduledMinutes) && weekOrdinary < rules.WeeklyCapMinutes;
                    if (ordinary)
                    {
                        dayOrdinary++;
                        weekOrdinary++;
                        if (night) ordinaryNight++;
                        else ordinaryDay++;
                    }
                    else if (night)
                    {
                        overtimeNight++;
                    }
                    else
                    {
                        overtimeDay++;
                    }

                    if (local.DayOfWeek == DayOfWeek.Sunday || holidays.Contains(localDate)) sundayHoliday++;
                }
            }

            var breakdown = new HoursBreakdown(ordinaryDay, ordinaryNight, overtimeDay, overtimeNight, sundayHoliday);
            days.Add(new DayHours(day.Date, breakdown.Total, day.ScheduledMinutes, breakdown));
            total = total.Add(breakdown);
        }

        return (days, total);
    }

    private static DateOnly StartOfWeek(DateOnly date) => date.AddDays(-(((int)date.DayOfWeek + 6) % 7));
}
