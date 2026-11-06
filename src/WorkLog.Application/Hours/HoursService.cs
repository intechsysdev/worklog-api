using WorkLog.Application.Abstractions;
using WorkLog.Application.Common;
using WorkLog.Application.Parameters;
using WorkLog.Application.Scheduling;
using WorkLog.Domain.Access;
using WorkLog.Domain.Hours;
using WorkLog.Domain.Organization;
using WorkLog.Domain.Parameters;
using WorkLog.Domain.Payroll;
using WorkLog.Domain.Requests;
using WorkLog.Domain.Scheduling;
using WorkLog.Domain.Workdays;

namespace WorkLog.Application.Hours;

public sealed record BreakdownDto(int OrdinaryDay, int OrdinaryNight, int OvertimeDay, int OvertimeNight, int SundayHoliday, int Total)
{
    public static BreakdownDto From(HoursBreakdown b) => new(b.OrdinaryDay, b.OrdinaryNight, b.OvertimeDay, b.OvertimeNight, b.SundayHoliday, b.Total);
}

public sealed record WeekDayDto(DateOnly Date, DayKind PlanKind, string? PlanLabel, int ScheduledMinutes, int WorkedMinutes, bool IsToday, bool InProgress);

public sealed record MyWeekDto(
    DateOnly From,
    DateOnly To,
    int WorkedMinutes,
    int WeeklyCapMinutes,
    int ScheduledMinutes,
    int RemainingMinutes,
    int ApprovedOvertimeMinutes,
    int OvertimeWeeklyMaxMinutes,
    BreakdownDto Breakdown,
    IReadOnlyList<WeekDayDto> Days,
    DateOnly ProvisionalUntil);

public enum HoursAlert
{
    None = 0,
    OvertimeInUse = 1,
    NearCap = 2,
    OverCap = 3
}

public sealed record TeamHoursRowDto(
    Guid MemberId,
    string Name,
    string Initials,
    string? GroupName,
    BreakdownDto Breakdown,
    int ApprovedOvertimeMinutes,
    HoursAlert Alert);

public sealed record TeamHoursDto(
    DateOnly From,
    DateOnly To,
    DateTimeOffset CalculatedAt,
    int WeeklyCapMinutes,
    int NearCapMinutes,
    int OvertimeWeeklyMaxMinutes,
    int OvertimeDailyMaxMinutes,
    IReadOnlyList<TeamHoursRowDto> Rows);

public interface IHoursService
{
    Task<OperationResult<MyWeekDto>> GetMyWeekAsync(DateOnly? date, CancellationToken ct = default);

    Task<OperationResult<TeamHoursDto>> GetTeamWeekAsync(DateOnly? date, Guid? branchId, bool onlyAlerts, CancellationToken ct = default);
}

/// <summary>Horas calculadas de una persona en un rango.</summary>
internal sealed record MemberHours(Member Member, IReadOnlyList<DayHours> Days, HoursBreakdown Total, IReadOnlyList<DayPlan> Plans);

/// <summary>Junta jornadas, turnos, festivos y reglas del país y aplica el cálculo del dominio.</summary>
internal interface IHoursEngine
{
    Task<IReadOnlyList<MemberHours>> ComputeAsync(IReadOnlyCollection<Member> people, DateOnly from, DateOnly to, CancellationToken ct = default);

    Task<HourRules> RulesAsync(Branch? branch, DateOnly date, CancellationToken ct = default);
}

internal sealed class HoursEngine(
    IWorkdayRepository workdays,
    IBranchRepository branches,
    IHolidayRepository holidays,
    IScheduleCalculator calculator,
    IParameterProvider parameters,
    TimeProvider clock) : IHoursEngine
{
    /// <summary>La franja nocturna termina a las 06:00 (Código Sustantivo del Trabajo, art. 160).</summary>
    private static readonly TimeOnly NightEnd = new(6, 0);

    public async Task<HourRules> RulesAsync(Branch? branch, DateOnly date, CancellationToken ct = default)
    {
        var p = await parameters.ForBranchAsync(branch, date, ct);
        return new HourRules(p[ParameterKey.WeeklyCapHours] * 60, new TimeOnly(p[ParameterKey.NightStartHour], 0), NightEnd);
    }

    public async Task<IReadOnlyList<MemberHours>> ComputeAsync(IReadOnlyCollection<Member> people, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        if (people.Count == 0) return [];

        // El cálculo de la semana necesita desde el lunes: el tope semanal arranca ahí.
        var weekStart = LocalTime.StartOfWeek(from);
        var now = clock.GetUtcNow();
        var plans = await calculator.PlansAsync(people, weekStart, to, ct);
        var list = await workdays.ListAsync(people.Select(p => p.Id).ToList(), weekStart, to, ct);
        var branchMap = (await branches.ListAsync(ct)).ToDictionary(b => b.Id);

        var result = new List<MemberHours>();
        foreach (var person in people)
        {
            var branch = person.BranchId is { } id ? branchMap.GetValueOrDefault(id) : null;
            var zone = LocalTime.Find(branch?.TimeZoneId);
            var rules = await RulesAsync(branch, to, ct);
            var country = branch?.CountryCode ?? Company.DefaultCountry;
            var holidaySet = (await holidays.ListAsync(country, weekStart, to.AddDays(1), ct)).Select(h => h.Date).ToHashSet();
            var memberPlans = plans[person.Id];

            var work = list
                .Where(w => w.MemberId == person.Id)
                .Select(w =>
                {
                    var plan = memberPlans.FirstOrDefault(p => p.Date == w.Date);
                    int? scheduled = plan is null || plan.Kind == DayKind.Unscheduled ? null : plan.ScheduledMinutes;
                    return new DayWork(w.Date, w.WorkedIntervals(now), scheduled);
                })
                .ToList();

            var (days, _) = HoursCalculator.Calculate(work, zone, rules, holidaySet);
            var inRange = days.Where(d => d.Date >= from && d.Date <= to).ToList();
            var total = inRange.Aggregate(HoursBreakdown.Zero, (acc, d) => acc.Add(d.Breakdown));
            result.Add(new MemberHours(person, inRange, total, memberPlans.Where(p => p.Date >= from).ToList()));
        }

        return result;
    }
}

internal sealed class HoursService(
    ICurrentActor actor,
    IMemberRepository members,
    IBranchRepository branches,
    IWorkGroupRepository groups,
    IWorkdayRepository workdays,
    IWorkRequestRepository requests,
    IHoursEngine engine,
    IParameterProvider parameters,
    TimeProvider clock) : IHoursService
{
    /// <summary>Margen bajo el tope desde el que se avisa "Cerca del tope".</summary>
    private const int NearCapMarginMinutes = 4 * 60;

    public async Task<OperationResult<MyWeekDto>> GetMyWeekAsync(DateOnly? date, CancellationToken ct = default)
    {
        var member = await members.FindAsync(actor.MemberId, ct);
        if (member is null) return OperationResult<MyWeekDto>.NotFound();

        var branch = member.BranchId is { } id ? await branches.FindAsync(id, ct) : null;
        var zone = LocalTime.Find(branch?.TimeZoneId);
        var today = LocalTime.Today(clock, zone);
        var monday = LocalTime.StartOfWeek(date ?? today);
        var sunday = monday.AddDays(6);

        var computed = (await engine.ComputeAsync([member], monday, sunday, ct))[0];
        var p = await parameters.ForBranchAsync(branch, monday, ct);
        var cap = p[ParameterKey.WeeklyCapHours] * 60;
        var scheduled = computed.Plans.Sum(d => d.ScheduledMinutes);
        var open = await workdays.FindOpenAsync(member.Id, ct);
        var approvedOvertime = (await requests.ListAsync([member.Id], monday, sunday, RequestStatus.Approved, ct))
            .Where(r => r.Type == RequestType.Overtime).Sum(r => r.OvertimeMinutes);

        var days = computed.Plans.Select(plan =>
        {
            var worked = computed.Days.FirstOrDefault(d => d.Date == plan.Date)?.WorkedMinutes ?? 0;
            return new WeekDayDto(plan.Date, plan.Kind, plan.Label, plan.ScheduledMinutes, worked, plan.Date == today, open?.Date == plan.Date);
        }).ToList();

        var period = Fortnight.Containing(today);
        return new MyWeekDto(monday, sunday, computed.Total.Total, cap, scheduled, Math.Max(0, scheduled - computed.Total.Total),
            approvedOvertime, p[ParameterKey.OvertimeWeeklyMaxHours] * 60, BreakdownDto.From(computed.Total), days, period.To);
    }

    public async Task<OperationResult<TeamHoursDto>> GetTeamWeekAsync(DateOnly? date, Guid? branchId, bool onlyAlerts, CancellationToken ct = default)
    {
        var branch = branchId is { } id ? await branches.FindAsync(id, ct) : null;
        if (branchId is not null && branch is null) return OperationResult<TeamHoursDto>.NotFound("La sede no existe.");

        var zone = LocalTime.Find(branch?.TimeZoneId);
        var monday = LocalTime.StartOfWeek(date ?? LocalTime.Today(clock, zone));
        var sunday = monday.AddDays(6);

        var people = (await members.ListMobileAsync(branchId, ct)).Where(m => m.Status == MemberStatus.Active).ToList();
        var computed = await engine.ComputeAsync(people, monday, sunday, ct);
        var p = await parameters.ForBranchAsync(branch, monday, ct);
        var cap = p[ParameterKey.WeeklyCapHours] * 60;
        var groupNames = (await groups.ListAsync(null, ct)).ToDictionary(g => g.Id, g => g.Name);
        var overtime = (await requests.ListAsync(people.Select(x => x.Id).ToList(), monday, sunday, RequestStatus.Approved, ct))
            .Where(r => r.Type == RequestType.Overtime)
            .GroupBy(r => r.MemberId)
            .ToDictionary(g => g.Key, g => g.Sum(r => r.OvertimeMinutes));

        var rows = computed
            .Select(c => new TeamHoursRowDto(c.Member.Id, c.Member.FullName, c.Member.Initials,
                c.Member.WorkGroupId is { } g ? groupNames.GetValueOrDefault(g) : null,
                BreakdownDto.From(c.Total), overtime.GetValueOrDefault(c.Member.Id), AlertFor(c.Total, cap)))
            .Where(r => !onlyAlerts || r.Alert != HoursAlert.None)
            .OrderByDescending(r => r.Alert)
            .ThenByDescending(r => r.Breakdown.Total)
            .ToList();

        return new TeamHoursDto(monday, sunday, clock.GetUtcNow(), cap, cap - NearCapMarginMinutes,
            p[ParameterKey.OvertimeWeeklyMaxHours] * 60, p[ParameterKey.OvertimeDailyMaxHours] * 60, rows);
    }

    internal static HoursAlert AlertFor(HoursBreakdown total, int cap) =>
        total.Total > cap ? HoursAlert.OverCap
        : total.Total >= cap - NearCapMarginMinutes ? HoursAlert.NearCap
        : total.Overtime > 0 ? HoursAlert.OvertimeInUse
        : HoursAlert.None;
}
