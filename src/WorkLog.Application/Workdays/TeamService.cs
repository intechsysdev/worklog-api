using WorkLog.Application.Abstractions;
using WorkLog.Application.Common;
using WorkLog.Application.Parameters;
using WorkLog.Application.Scheduling;
using WorkLog.Domain.Access;
using WorkLog.Domain.Geo;
using WorkLog.Domain.Parameters;
using WorkLog.Domain.Scheduling;
using WorkLog.Domain.Workdays;

namespace WorkLog.Application.Workdays;

/// <summary>Estado de una persona en "Equipo hoy".</summary>
public enum TeamStatus
{
    InActivity = 1,
    Paused = 2,
    Idle = 3,
    NotStarted = 4,
    Absence = 5,
    DayOff = 6,
    Closed = 7
}

/// <summary>Filtro de la tabla: los mismos grupos que las pestañas del panel.</summary>
public enum TeamFilter
{
    All = 0,
    Working = 1,
    Idle = 2,
    NotStarted = 3
}

public sealed record TeamKpisDto(
    int Total,
    int InActivity,
    int Paused,
    int Idle,
    int IdleOverThreshold,
    int NotStarted,
    int Late,
    int WithAbsence,
    int Closed);

public sealed record TeamMemberRowDto(
    Guid MemberId,
    string Name,
    string Initials,
    string? GroupName,
    TeamStatus Status,
    string? ActivityName,
    string? WorkOrderNumber,
    string? SiteName,
    DateTimeOffset? Since,
    int? IdleMinutes,
    DayKind PlanKind,
    string? PlanLabel,
    TimeOnly? ShiftStart,
    TimeOnly? ShiftEnd,
    int LateMinutes,
    int WeekMinutes,
    GeofenceStatus Geofence,
    double? Accuracy,
    string? DeviceModel,
    DateTimeOffset? LastSeenAt,
    bool? DeviceOnline,
    int? BatteryPercent,
    DateTimeOffset? DeviceLastSeenAt);

public sealed record TeamTodayDto(
    DateTimeOffset UpdatedAt,
    DateOnly Date,
    Guid? BranchId,
    int InactivityAlertMinutes,
    int WeeklyCapMinutes,
    TeamKpisDto Kpis,
    IReadOnlyList<TeamMemberRowDto> Rows);

public interface ITeamService
{
    Task<OperationResult<TeamTodayDto>> GetTodayAsync(Guid? branchId, TeamFilter filter, string? search, CancellationToken ct = default);
}

internal sealed class TeamService(
    IMemberRepository members,
    IBranchRepository branches,
    IWorkGroupRepository groups,
    IWorkdayRepository workdays,
    IScheduleCalculator calculator,
    IParameterProvider parameters,
    Devices.IDeviceOrchestrator devices,
    TimeProvider clock) : ITeamService
{
    public async Task<OperationResult<TeamTodayDto>> GetTodayAsync(Guid? branchId, TeamFilter filter, string? search, CancellationToken ct = default)
    {
        var branch = branchId is { } id ? await branches.FindAsync(id, ct) : null;
        if (branchId is not null && branch is null) return OperationResult<TeamTodayDto>.NotFound("La sede no existe.");

        var zone = LocalTime.Find(branch?.TimeZoneId);
        var now = clock.GetUtcNow();
        var today = LocalTime.Today(clock, zone);
        var monday = LocalTime.StartOfWeek(today);
        var settings = await parameters.ForBranchAsync(branch, today, ct);
        var idleThreshold = settings[ParameterKey.InactivityAlertMinutes];

        var people = (await members.ListMobileAsync(branchId, ct)).Where(m => m.Status == MemberStatus.Active).ToList();
        var ids = people.Select(p => p.Id).ToList();
        var plans = await calculator.PlansAsync(people, today, today, ct);
        var week = await workdays.ListAsync(ids, monday, today, ct);
        var groupNames = (await groups.ListAsync(null, ct)).ToDictionary(g => g.Id, g => g.Name);
        var deviceMap = await devices.DevicesAsync(ct);

        var rows = people.Select(person =>
        {
            var plan = plans[person.Id][0];
            var mine = week.Where(w => w.MemberId == person.Id).ToList();
            var todayWorkday = mine.Where(w => w.Date == today).MaxBy(w => w.StartedAt);
            var weekMinutes = mine.Sum(w => WorkdayTimeline.Build(w, now).Summary.WorkedMinutes);
            var row = BuildRow(person, plan, todayWorkday, weekMinutes, groupNames, now, zone, settings[ParameterKey.LateToleranceMinutes]);
            return person.DeviceId is { } deviceId && deviceMap.TryGetValue(deviceId, out var device)
                ? row with { DeviceOnline = device.Online, BatteryPercent = device.BatteryPercent, DeviceLastSeenAt = device.LastSeenAt }
                : row;
        }).ToList();

        var kpis = new TeamKpisDto(
            rows.Count,
            rows.Count(r => r.Status == TeamStatus.InActivity),
            rows.Count(r => r.Status == TeamStatus.Paused),
            rows.Count(r => r.Status == TeamStatus.Idle),
            rows.Count(r => r.Status == TeamStatus.Idle && r.IdleMinutes > idleThreshold),
            rows.Count(r => r.Status is TeamStatus.NotStarted or TeamStatus.Absence),
            rows.Count(r => r.Status == TeamStatus.NotStarted && r.LateMinutes > 0),
            rows.Count(r => r.Status == TeamStatus.Absence),
            rows.Count(r => r.Status == TeamStatus.Closed));

        var term = Domain.Common.Text.Key(search);
        var visible = rows
            .Where(r => filter switch
            {
                TeamFilter.Working => r.Status is TeamStatus.InActivity or TeamStatus.Paused,
                TeamFilter.Idle => r.Status == TeamStatus.Idle,
                TeamFilter.NotStarted => r.Status is TeamStatus.NotStarted or TeamStatus.Absence,
                _ => true
            })
            .Where(r => term.Length == 0 ||
                        Domain.Common.Text.Key(r.Name).Contains(term, StringComparison.Ordinal) ||
                        Domain.Common.Text.Key(r.WorkOrderNumber).Contains(term, StringComparison.Ordinal))
            .OrderBy(r => StatusOrder(r.Status))
            .ThenByDescending(r => r.IdleMinutes ?? 0)
            .ThenBy(r => r.Name)
            .ToList();

        return new TeamTodayDto(now, today, branchId, idleThreshold, settings[ParameterKey.WeeklyCapHours] * 60, kpis, visible);
    }

    private static TeamMemberRowDto BuildRow(Member person, DayPlan plan, Workday? workday, int weekMinutes,
        IReadOnlyDictionary<Guid, string> groupNames, DateTimeOffset now, TimeZoneInfo zone, int toleranceMinutes)
    {
        var group = person.WorkGroupId is { } g ? groupNames.GetValueOrDefault(g) : null;
        TeamStatus status;
        string? activityName = null, workOrder = null, site = null;
        DateTimeOffset? since = null;
        int? idleMinutes = null;
        var geofence = GeofenceStatus.NotApplicable;
        double? accuracy = null;
        var late = 0;

        if (workday is null)
        {
            status = plan.Kind switch
            {
                DayKind.Absence => TeamStatus.Absence,
                _ when plan.IsWorkingDay => TeamStatus.NotStarted,
                _ => TeamStatus.DayOff
            };

            // Tarde: pasó la tolerancia y no ha iniciado. Los minutos se cuentan desde la hora del turno.
            if (status == TeamStatus.NotStarted && plan.Start is { } start)
            {
                var shiftStart = ShiftWindow.ToInstant(plan.Date, start, zone);
                if (now > shiftStart.AddMinutes(toleranceMinutes))
                    late = (int)(now - shiftStart).TotalMinutes;
            }
        }
        else if (!workday.IsOpen)
        {
            status = TeamStatus.Closed;
            since = workday.EndedAt;
            late = workday.LateMinutes;
        }
        else if (workday.CurrentActivity is { } current)
        {
            status = current.Status == ActivityStatus.Paused ? TeamStatus.Paused : TeamStatus.InActivity;
            activityName = current.Name;
            workOrder = current.WorkOrderNumber;
            site = current.WorkSiteName;
            since = current.Status == ActivityStatus.Paused ? current.Pauses[^1].StartedAt : current.StartedAt;
            geofence = current.StartGeofence;
            accuracy = current.StartLocation.AccuracyMeters;
            late = workday.LateMinutes;
        }
        else
        {
            status = TeamStatus.Idle;
            since = workday.Activities.Select(a => a.EndedAt ?? a.StartedAt).Append(workday.StartedAt).Max();
            idleMinutes = (int)Math.Max(0, (now - since.Value).TotalMinutes);
            geofence = workday.StartGeofence;
            accuracy = workday.StartLocation.AccuracyMeters;
            late = workday.LateMinutes;
        }

        return new TeamMemberRowDto(person.Id, person.FullName, person.Initials, group, status, activityName, workOrder, site,
            since, idleMinutes, plan.Kind, plan.Label, plan.Start, plan.End, late, weekMinutes, geofence, accuracy,
            person.DeviceModel, person.LastSeenAt, null, null, null);
    }

    private static int StatusOrder(TeamStatus status) => status switch
    {
        TeamStatus.Idle => 0,
        TeamStatus.NotStarted => 1,
        TeamStatus.InActivity => 2,
        TeamStatus.Paused => 3,
        TeamStatus.Absence => 4,
        TeamStatus.Closed => 5,
        _ => 6
    };
}
