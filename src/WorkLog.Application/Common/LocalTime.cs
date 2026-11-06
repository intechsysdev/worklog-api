namespace WorkLog.Application.Common;

/// <summary>
/// Hora local de una sede. "Hoy" depende de la zona horaria: a las 20:00 en Bogotá ya es mañana
/// en UTC, y la jornada pertenece al día local.
/// </summary>
public static class LocalTime
{
    public const string DefaultTimeZoneId = "America/Bogota";

    public static TimeZoneInfo Find(string? timeZoneId) =>
        !string.IsNullOrWhiteSpace(timeZoneId) && TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId, out var zone)
            ? zone
            : TimeZoneInfo.FindSystemTimeZoneById(DefaultTimeZoneId);

    public static DateTimeOffset Now(TimeProvider clock, TimeZoneInfo zone) => TimeZoneInfo.ConvertTime(clock.GetUtcNow(), zone);

    public static DateOnly Today(TimeProvider clock, TimeZoneInfo zone) => DateOnly.FromDateTime(Now(clock, zone).DateTime);

    public static DateOnly DateOf(DateTimeOffset instant, TimeZoneInfo zone) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);

    public static DateOnly StartOfWeek(DateOnly date) => date.AddDays(-(((int)date.DayOfWeek + 6) % 7));
}
