namespace WorkLog.Domain.Scheduling;

/// <summary>Días de la semana de una plantilla, como banderas (L M X J V S D).</summary>
[Flags]
public enum WeekDays
{
    None = 0,
    Monday = 1,
    Tuesday = 2,
    Wednesday = 4,
    Thursday = 8,
    Friday = 16,
    Saturday = 32,
    Sunday = 64,
    MondayToFriday = Monday | Tuesday | Wednesday | Thursday | Friday,
    MondayToSaturday = MondayToFriday | Saturday,
    All = MondayToSaturday | Sunday
}

public static class WeekDaysExtensions
{
    private static readonly (WeekDays Day, string Letter)[] Order =
    [
        (WeekDays.Monday, "L"), (WeekDays.Tuesday, "M"), (WeekDays.Wednesday, "X"), (WeekDays.Thursday, "J"),
        (WeekDays.Friday, "V"), (WeekDays.Saturday, "S"), (WeekDays.Sunday, "D")
    ];

    public static WeekDays ToWeekDay(this DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => WeekDays.Monday,
        DayOfWeek.Tuesday => WeekDays.Tuesday,
        DayOfWeek.Wednesday => WeekDays.Wednesday,
        DayOfWeek.Thursday => WeekDays.Thursday,
        DayOfWeek.Friday => WeekDays.Friday,
        DayOfWeek.Saturday => WeekDays.Saturday,
        _ => WeekDays.Sunday
    };

    public static bool Includes(this WeekDays days, DayOfWeek day) => (days & day.ToWeekDay()) != 0;

    public static int CountDays(this WeekDays days) => Order.Count(o => (days & o.Day) != 0);

    /// <summary>Letras de los días incluidos, p. ej. "LMXJV".</summary>
    public static string ToLetters(this WeekDays days) =>
        string.Concat(Order.Where(o => (days & o.Day) != 0).Select(o => o.Letter));
}
