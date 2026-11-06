using WorkLog.Application.Abstractions;
using WorkLog.Application.Common;
using WorkLog.Application.Devices;
using WorkLog.Application.Parameters;
using WorkLog.Domain.Geo;
using WorkLog.Domain.Parameters;
using WorkLog.Domain.Requests;
using WorkLog.Domain.Workdays;

namespace WorkLog.Application.Workdays;

/// <summary>Procesos del sistema sobre las jornadas de una empresa (los dispara un temporizador).</summary>
public interface IWorkdayAutomation
{
    /// <summary>Cierra las jornadas que siguen abiertas pasado el fin del turno más la gracia.</summary>
    Task<int> CloseOverdueAsync(CancellationToken ct = default);
}

internal sealed class WorkdayAutomation(
    IOpenWorkdayQueries openWorkdays,
    IMemberRepository members,
    IBranchRepository branches,
    IWorkRequestRepository requests,
    IParameterProvider parameters,
    IDeviceOrchestrator devices,
    IAuditTrail audit,
    IUnitOfWork unitOfWork,
    TimeProvider clock) : IWorkdayAutomation
{
    public async Task<int> CloseOverdueAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var closed = 0;

        foreach (var workday in await openWorkdays.ListOpenAsync(ct))
        {
            var member = await members.FindAsync(workday.MemberId, ct);
            if (member is null) continue;

            var branch = workday.BranchId is { } id ? await branches.FindAsync(id, ct) : null;
            var zone = LocalTime.Find(branch?.TimeZoneId);
            var grace = (await parameters.ForBranchAsync(branch, workday.Date, ct))[ParameterKey.AutoCloseGraceMinutes];
            var end = await ScheduledEndAsync(workday, zone, ct);

            // Sin horario: se cierra cuando ya pasó el día, a la hora del último evento.
            var dueAt = end is { } e ? e.AddMinutes(grace) : ShiftWindow.ToInstant(workday.Date.AddDays(1), TimeOnly.MinValue, zone);
            if (now < dueAt) continue;

            var closeAt = end is { } shiftEnd && shiftEnd > workday.LastEventAt ? shiftEnd : workday.LastEventAt;
            if (workday.Close(closeAt, null, GeofenceStatus.NoLocation, null, CloseKind.Automatic).IsFailure) continue;

            audit.RecordSystem(workday.TenantId, "workday.auto_closed",
                $"Regla de cierre automático: cerró la jornada de {member.FullName} a las {TimeZoneInfo.ConvertTime(closeAt, zone):HH\\:mm}. Queda pendiente de revisión.",
                "Registros", workday.Id.ToString());

            await devices.OnWorkdayClosedAsync(member, ct);

            try
            {
                await unitOfWork.SaveChangesAsync(ct);
                closed++;
            }
            catch (PersistenceConflictException)
            {
                // El teléfono cerró la jornada al mismo tiempo: ganó el cierre manual, que es lo correcto.
            }
        }

        return closed;
    }

    /// <summary>Fin del turno, corrido hasta el fin de las horas extra aprobadas para ese día.</summary>
    private async Task<DateTimeOffset?> ScheduledEndAsync(Workday workday, TimeZoneInfo zone, CancellationToken ct)
    {
        if (workday.ScheduledEnd is not { } scheduled) return null;

        var end = ShiftWindow.ToInstant(workday.Date, scheduled, zone);
        var overtime = (await requests.ListAsync([workday.MemberId], workday.Date, workday.Date, RequestStatus.Approved, ct))
            .Where(r => r.Type == RequestType.Overtime && r.OvertimeTo is not null)
            .Select(r => ShiftWindow.ToInstant(workday.Date, r.OvertimeTo!.Value, zone))
            .DefaultIfEmpty(end)
            .Max();

        return overtime > end ? overtime : end;
    }
}
