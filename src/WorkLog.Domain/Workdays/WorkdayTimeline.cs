using WorkLog.Domain.Activities;

namespace WorkLog.Domain.Workdays;

public enum SegmentKind
{
    Activity = 1,

    /// <summary>Hueco entre actividades: cuenta como "sin actividad".</summary>
    Idle = 2
}

/// <summary>Tramo de la barra del día. <see cref="End"/> es null si sigue en curso.</summary>
public sealed record TimelineSegment(
    SegmentKind Kind,
    DateTimeOffset Start,
    DateTimeOffset? End,
    int Minutes,
    string Title,
    string Color,
    TimeClass? TimeClass,
    Guid? ActivityId);

/// <summary>Totales del día. "Sin actividad" suma los huecos y las actividades improductivas.</summary>
public sealed record DaySummary(
    int ActivityCount,
    int ProductiveMinutes,
    int PaidNonProductiveMinutes,
    int UnproductiveMinutes,
    int IdleMinutes,
    int PausedMinutes,
    int ElapsedMinutes)
{
    public int WithoutActivityMinutes => IdleMinutes + UnproductiveMinutes;

    /// <summary>Tiempo pagado: todo lo trabajado menos pausas (el almuerzo es una pausa).</summary>
    public int WorkedMinutes => ProductiveMinutes + PaidNonProductiveMinutes + UnproductiveMinutes + IdleMinutes;
}

/// <summary>Arma la barra del día y sus totales a partir de la jornada.</summary>
public static class WorkdayTimeline
{
    public const string IdleColor = "#DA830B";

    /// <summary>Un hueco menor a un minuto es ruido del reloj, no inactividad.</summary>
    private static readonly TimeSpan MinGap = TimeSpan.FromMinutes(1);

    public static (IReadOnlyList<TimelineSegment> Segments, DaySummary Summary) Build(Workday workday, DateTimeOffset now)
    {
        var end = workday.EndedAt ?? now;
        var segments = new List<TimelineSegment>();
        var cursor = workday.StartedAt;

        foreach (var activity in workday.Activities.OrderBy(a => a.StartedAt))
        {
            AddIdle(segments, cursor, activity.StartedAt);

            var activityEnd = activity.EndedAt ?? end;
            segments.Add(new TimelineSegment(
                SegmentKind.Activity,
                activity.StartedAt,
                activity.EndedAt,
                Minutes(activity.WorkedTime(activityEnd)),
                activity.Name,
                activity.Color,
                activity.TimeClass,
                activity.Id));

            if (activityEnd > cursor) cursor = activityEnd;
        }

        // Hueco final: solo si no hay nada en curso (si lo hay, el cursor ya llegó al final).
        AddIdle(segments, cursor, end);

        var byClass = workday.Activities
            .GroupBy(a => a.TimeClass)
            .ToDictionary(g => g.Key, g => g.Sum(a => Minutes(a.WorkedTime(a.EndedAt ?? end))));

        var summary = new DaySummary(
            ActivityCount: workday.Activities.Count,
            ProductiveMinutes: byClass.GetValueOrDefault(TimeClass.Productive),
            PaidNonProductiveMinutes: byClass.GetValueOrDefault(TimeClass.PaidNonProductive),
            UnproductiveMinutes: byClass.GetValueOrDefault(TimeClass.Unproductive),
            IdleMinutes: segments.Where(s => s.Kind == SegmentKind.Idle).Sum(s => s.Minutes),
            PausedMinutes: workday.Activities.Sum(a => Minutes(a.PausedTime(a.EndedAt ?? end))),
            ElapsedMinutes: Minutes(end - workday.StartedAt));

        return (segments, summary);
    }

    private static void AddIdle(List<TimelineSegment> segments, DateTimeOffset from, DateTimeOffset to)
    {
        if (to - from < MinGap) return;
        segments.Add(new TimelineSegment(SegmentKind.Idle, from, to, Minutes(to - from), "Sin actividad", IdleColor, null, null));
    }

    private static int Minutes(TimeSpan span) => span <= TimeSpan.Zero ? 0 : (int)Math.Floor(span.TotalMinutes);
}
