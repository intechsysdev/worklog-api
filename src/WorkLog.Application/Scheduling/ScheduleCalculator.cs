using WorkLog.Application.Abstractions;
using WorkLog.Domain.Access;
using WorkLog.Domain.Organization;
using WorkLog.Domain.Scheduling;

namespace WorkLog.Application.Scheduling;

/// <summary>Calcula el plan de cada día para varias personas con una sola carga de datos.</summary>
public interface IScheduleCalculator
{
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<DayPlan>>> PlansAsync(IReadOnlyCollection<Member> people, DateOnly from, DateOnly to, CancellationToken ct = default);

    Task<DayPlan> PlanAsync(Member member, DateOnly date, CancellationToken ct = default);

    /// <summary>Próximo día con turno después de <paramref name="after"/>, buscando hasta 60 días.</summary>
    Task<DayPlan?> NextWorkingDayAsync(Member member, DateOnly after, CancellationToken ct = default);
}

internal sealed class ScheduleCalculator(
    IScheduleAssignmentRepository assignments,
    IScheduleTemplateRepository templates,
    IShiftOverrideRepository overrides,
    IAbsenceRepository absences,
    IHolidayRepository holidays,
    IBranchRepository branches) : IScheduleCalculator
{
    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<DayPlan>>> PlansAsync(
        IReadOnlyCollection<Member> people, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        if (people.Count == 0) return new Dictionary<Guid, IReadOnlyList<DayPlan>>();

        var ids = people.Select(p => p.Id).ToList();
        var assignmentList = await assignments.ListAsync(ct);
        var templateMap = (await templates.ListAsync(ct)).ToDictionary(t => t.Id);
        var overrideList = await overrides.ListAsync(ids, from, to, ct);
        var absenceList = await absences.ListApprovedAsync(ids, from, to, ct);
        var branchMap = (await branches.ListAsync(ct)).ToDictionary(b => b.Id);

        var holidaysByCountry = new Dictionary<string, IReadOnlyList<Holiday>>();
        foreach (var country in branchMap.Values.Select(b => b.CountryCode).Append(Company.DefaultCountry).Distinct())
            holidaysByCountry[country] = await holidays.ListAsync(country, from, to, ct);

        var result = new Dictionary<Guid, IReadOnlyList<DayPlan>>();
        foreach (var person in people)
        {
            var country = person.BranchId is { } b && branchMap.TryGetValue(b, out var branch) ? branch.CountryCode : Company.DefaultCountry;
            var context = new ScheduleContext(person.Id, person.WorkGroupId, person.BranchId, assignmentList, templateMap,
                overrideList, absenceList, holidaysByCountry[country]);
            result[person.Id] = ShiftResolver.ResolveRange(from, to, context);
        }

        return result;
    }

    public async Task<DayPlan> PlanAsync(Member member, DateOnly date, CancellationToken ct = default) =>
        (await PlansAsync([member], date, date, ct))[member.Id][0];

    public async Task<DayPlan?> NextWorkingDayAsync(Member member, DateOnly after, CancellationToken ct = default)
    {
        var plans = (await PlansAsync([member], after.AddDays(1), after.AddDays(60), ct))[member.Id];
        return plans.FirstOrDefault(p => p.IsWorkingDay);
    }
}
