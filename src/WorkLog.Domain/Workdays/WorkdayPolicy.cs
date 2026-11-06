using WorkLog.Domain.Common;
using WorkLog.Domain.Scheduling;

namespace WorkLog.Domain.Workdays;

/// <summary>Decisión sobre un intento de iniciar la jornada.</summary>
public sealed record StartDecision(DateTimeOffset StartAt, bool Adjusted, int LateMinutes);

/// <summary>Ventana del turno en horas absolutas, ya convertida desde la zona horaria de la sede.</summary>
public sealed record ShiftWindow(DateTimeOffset OpensAt, DateTimeOffset ShiftStart, DateTimeOffset ShiftEnd)
{
    public static ShiftWindow? For(DayPlan plan, TimeZoneInfo timeZone, int earlyStartMinutes)
    {
        if (!plan.IsWorkingDay) return null;

        var start = ToInstant(plan.Date, plan.Start!.Value, timeZone);
        var end = ToInstant(plan.Date, plan.End!.Value, timeZone);
        return new ShiftWindow(start.AddMinutes(-earlyStartMinutes), start, end);
    }

    public static DateTimeOffset ToInstant(DateOnly date, TimeOnly time, TimeZoneInfo timeZone)
    {
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, timeZone.GetUtcOffset(local));
    }
}

/// <summary>Reglas para abrir la jornada: solo con turno y dentro de la ventana permitida.</summary>
public static class WorkdayPolicy
{
    /// <param name="requestedAt">Hora en que el usuario tocó "Iniciar jornada".</param>
    /// <param name="queuedOffline">El evento llegó después, desde la cola del teléfono.</param>
    public static Result<StartDecision> EvaluateStart(
        DayPlan plan,
        DateTimeOffset requestedAt,
        TimeZoneInfo timeZone,
        int earlyStartMinutes,
        int lateToleranceMinutes,
        bool queuedOffline)
    {
        if (!plan.IsWorkingDay)
        {
            var reason = plan.Kind switch
            {
                DayKind.Absence => $"Hoy tienes {plan.Label?.ToLowerInvariant() ?? "una novedad"}: no puedes iniciar jornada.",
                DayKind.Holiday => $"Hoy es festivo ({plan.Label}): no tienes turno.",
                DayKind.Unscheduled => "No tienes horario asignado. Habla con tu supervisor.",
                _ => "Hoy no tienes turno: no puedes iniciar jornada."
            };
            return DomainError.Forbidden("workday.no_shift", reason);
        }

        var window = ShiftWindow.For(plan, timeZone, earlyStartMinutes)!;
        var startAt = requestedAt;
        var adjusted = false;

        if (requestedAt < window.OpensAt)
        {
            // Si el inicio llegó desde la cola sin conexión, ya ocurrió: se registra al abrir la
            // ventana en vez de perderlo, y el usuario puede pedir un ajuste si no está de acuerdo.
            if (!queuedOffline)
            {
                var opens = TimeZoneInfo.ConvertTime(window.OpensAt, timeZone);
                var shift = TimeZoneInfo.ConvertTime(window.ShiftStart, timeZone);
                return DomainError.Forbidden("workday.too_early",
                    $"Tu turno empieza a las {shift:HH\\:mm}. Puedes iniciar desde las {opens:HH\\:mm}.");
            }

            startAt = window.OpensAt;
            adjusted = true;
        }

        if (startAt >= window.ShiftEnd)
            return DomainError.Forbidden("workday.shift_over", "Tu turno de hoy ya terminó.");

        var lateMinutes = startAt > window.ShiftStart.AddMinutes(lateToleranceMinutes)
            ? (int)Math.Floor((startAt - window.ShiftStart).TotalMinutes)
            : 0;

        return new StartDecision(startAt, adjusted, lateMinutes);
    }
}
