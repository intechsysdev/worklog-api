using Microsoft.EntityFrameworkCore;
using WorkLog.Application.Abstractions;
using WorkLog.Application.Common;
using WorkLog.Domain.Absences;
using WorkLog.Domain.Access;
using WorkLog.Domain.Activities;
using WorkLog.Domain.Auditing;
using WorkLog.Domain.Common;
using WorkLog.Domain.Organization;
using WorkLog.Domain.Parameters;
using WorkLog.Domain.Privacy;
using WorkLog.Domain.Scheduling;
using WorkLog.Domain.Sync;
using WorkLog.Domain.Workdays;

namespace WorkLog.Infrastructure.Persistence.Repositories;

internal class Repository<T>(WorkLogDbContext db) : IRepository<T> where T : Entity
{
    protected WorkLogDbContext Db { get; } = db;
    protected DbSet<T> Set => Db.Set<T>();

    public virtual Task<T?> FindAsync(Guid id, CancellationToken ct = default) =>
        Set.FirstOrDefaultAsync(e => e.Id == id, ct);

    public virtual async Task<IReadOnlyList<T>> FindManyAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default) =>
        ids.Count == 0 ? [] : await Set.Where(e => ids.Contains(e.Id)).ToListAsync(ct);

    public void Add(T entity) => Set.Add(entity);

    public void Remove(T entity) => Set.Remove(entity);
}

internal sealed class CompanyRepository(WorkLogDbContext db) : ICompanyRepository
{
    public Task<Company?> FindAsync(Guid tenantId, CancellationToken ct = default) =>
        db.Companies.FirstOrDefaultAsync(c => c.Id == tenantId, ct);

    public void Add(Company company) => db.Companies.Add(company);
}

internal sealed class BranchRepository(WorkLogDbContext db) : Repository<Branch>(db), IBranchRepository
{
    public async Task<IReadOnlyList<Branch>> ListAsync(CancellationToken ct = default) => await Set.ToListAsync(ct);

    public Task<bool> NameExistsAsync(string nameKey, Guid? exceptId, CancellationToken ct = default) =>
        Set.AnyAsync(b => b.NameKey == nameKey && b.Id != exceptId, ct);
}

internal sealed class WorkGroupRepository(WorkLogDbContext db) : Repository<WorkGroup>(db), IWorkGroupRepository
{
    public async Task<IReadOnlyList<WorkGroup>> ListAsync(Guid? branchId = null, CancellationToken ct = default) =>
        await Set.Where(g => branchId == null || g.BranchId == branchId).ToListAsync(ct);

    public Task<bool> NameExistsAsync(string nameKey, Guid branchId, Guid? exceptId, CancellationToken ct = default) =>
        Set.AnyAsync(g => g.NameKey == nameKey && g.BranchId == branchId && g.Id != exceptId, ct);
}

internal sealed class WorkSiteRepository(WorkLogDbContext db) : Repository<WorkSite>(db), IWorkSiteRepository
{
    public async Task<IReadOnlyList<WorkSite>> ListAsync(bool includeInactive = false, CancellationToken ct = default) =>
        await Set.Where(s => includeInactive || s.IsActive).ToListAsync(ct);

    public Task<bool> NameExistsAsync(string nameKey, Guid? exceptId, CancellationToken ct = default) =>
        Set.AnyAsync(s => s.NameKey == nameKey && s.Id != exceptId, ct);
}

internal sealed class RoleRepository(WorkLogDbContext db) : Repository<Role>(db), IRoleRepository
{
    public async Task<IReadOnlyList<Role>> ListAsync(CancellationToken ct = default) => await Set.ToListAsync(ct);

    public Task<Role?> FindBySystemKeyAsync(string systemKey, CancellationToken ct = default) =>
        Set.FirstOrDefaultAsync(r => r.SystemKey == systemKey, ct);

    public Task<bool> NameExistsAsync(string nameKey, Guid? exceptId, CancellationToken ct = default) =>
        Set.AnyAsync(r => r.NameKey == nameKey && r.Id != exceptId, ct);
}

internal sealed class MemberRepository(WorkLogDbContext db) : Repository<Member>(db), IMemberRepository
{
    public Task<Member?> FindByOneUserAsync(Guid oneUserId, CancellationToken ct = default) =>
        Set.FirstOrDefaultAsync(m => m.OneUserId == oneUserId, ct);

    public Task<Member?> FindByEmailAsync(string normalizedEmail, CancellationToken ct = default) =>
        Set.FirstOrDefaultAsync(m => m.Email == normalizedEmail, ct);

    public async Task<PagedResult<Member>> SearchAsync(MemberFilter filter, CancellationToken ct = default)
    {
        var query = Set.AsQueryable();

        if (filter.BranchId is { } branch) query = query.Where(m => m.BranchId == branch);
        if (filter.WorkGroupId is { } group) query = query.Where(m => m.WorkGroupId == group);
        if (filter.RoleId is { } role) query = query.Where(m => m.RoleId == role);
        if (filter.Status is { } status) query = query.Where(m => m.Status == status);
        if (filter.Usage is { } usage) query = query.Where(m => m.Usage == usage);

        if (filter.Page.Search is { } search)
        {
            var term = $"%{search}%";
            query = query.Where(m => EF.Functions.Like(m.FullName, term) || EF.Functions.Like(m.Email, term));
        }

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderBy(m => m.Status == MemberStatus.Inactive)
            .ThenBy(m => m.FullName)
            .Skip(filter.Page.Skip)
            .Take(filter.Page.PageSize)
            .ToListAsync(ct);

        return new PagedResult<Member>(items, filter.Page.Page, filter.Page.PageSize, total);
    }

    public async Task<IReadOnlyList<Member>> ListMobileAsync(Guid? branchId, CancellationToken ct = default) =>
        await Set
            .Where(m => m.Usage == MemberUsage.Mobile && m.Status != MemberStatus.Inactive)
            .Where(m => branchId == null || m.BranchId == branchId)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Member>> ListAllAsync(CancellationToken ct = default) => await Set.ToListAsync(ct);

    public Task<int> CountByBranchAsync(Guid branchId, CancellationToken ct = default) =>
        Set.CountAsync(m => m.BranchId == branchId && m.Status != MemberStatus.Inactive, ct);

    public Task<int> CountByGroupAsync(Guid groupId, CancellationToken ct = default) =>
        Set.CountAsync(m => m.WorkGroupId == groupId && m.Status != MemberStatus.Inactive, ct);

    public async Task<IReadOnlyDictionary<Guid, int>> CountPerRoleAsync(CancellationToken ct = default) =>
        await Set.Where(m => m.Status != MemberStatus.Inactive)
            .GroupBy(m => m.RoleId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
}

internal sealed class ScheduleTemplateRepository(WorkLogDbContext db) : Repository<ScheduleTemplate>(db), IScheduleTemplateRepository
{
    public async Task<IReadOnlyList<ScheduleTemplate>> ListAsync(CancellationToken ct = default) => await Set.ToListAsync(ct);

    public Task<bool> NameExistsAsync(string nameKey, Guid? exceptId, CancellationToken ct = default) =>
        Set.AnyAsync(t => t.NameKey == nameKey && t.Id != exceptId, ct);
}

internal sealed class ScheduleAssignmentRepository(WorkLogDbContext db) : Repository<ScheduleAssignment>(db), IScheduleAssignmentRepository
{
    public async Task<IReadOnlyList<ScheduleAssignment>> ListAsync(CancellationToken ct = default) => await Set.ToListAsync(ct);

    public Task<bool> ExistsAsync(AssignmentLevel level, Guid targetId, DateOnly effectiveFrom, CancellationToken ct = default) =>
        Set.AnyAsync(a => a.Level == level && a.TargetId == targetId && a.EffectiveFrom == effectiveFrom, ct);
}

internal sealed class ShiftOverrideRepository(WorkLogDbContext db) : Repository<ShiftOverride>(db), IShiftOverrideRepository
{
    public async Task<IReadOnlyList<ShiftOverride>> ListAsync(IReadOnlyCollection<Guid> memberIds, DateOnly from, DateOnly to, CancellationToken ct = default) =>
        await Set.Where(o => memberIds.Contains(o.MemberId) && o.Date >= from && o.Date <= to).ToListAsync(ct);
}

internal sealed class AbsenceRepository(WorkLogDbContext db) : Repository<Absence>(db), IAbsenceRepository
{
    public async Task<IReadOnlyList<Absence>> ListApprovedAsync(IReadOnlyCollection<Guid> memberIds, DateOnly from, DateOnly to, CancellationToken ct = default) =>
        await Set.Where(a => memberIds.Contains(a.MemberId) && a.Status == AbsenceStatus.Approved && a.From <= to && a.To >= from)
            .ToListAsync(ct);
}

internal sealed class HolidayRepository(WorkLogDbContext db) : IHolidayRepository
{
    public async Task<IReadOnlyList<Holiday>> ListAsync(string countryCode, DateOnly from, DateOnly to, CancellationToken ct = default) =>
        await db.Holidays.AsNoTracking()
            .Where(h => h.CountryCode == countryCode && h.Date >= from && h.Date <= to)
            .OrderBy(h => h.Date)
            .ToListAsync(ct);
}

internal sealed class ActivityCategoryRepository(WorkLogDbContext db) : Repository<ActivityCategory>(db), IActivityCategoryRepository
{
    public async Task<IReadOnlyList<ActivityCategory>> ListAsync(CancellationToken ct = default) => await Set.ToListAsync(ct);

    public Task<bool> NameExistsAsync(string nameKey, Guid? exceptId, CancellationToken ct = default) =>
        Set.AnyAsync(c => c.NameKey == nameKey && c.Id != exceptId, ct);
}

internal sealed class ActivityTypeRepository(WorkLogDbContext db) : Repository<ActivityType>(db), IActivityTypeRepository
{
    public async Task<IReadOnlyList<ActivityType>> ListAsync(bool activeOnly = false, CancellationToken ct = default) =>
        await Set.Where(t => !activeOnly || t.IsActive).ToListAsync(ct);

    public Task<bool> NameExistsAsync(string nameKey, Guid? exceptId, CancellationToken ct = default) =>
        Set.AnyAsync(t => t.NameKey == nameKey && t.Id != exceptId, ct);
}

internal sealed class OutcomeSettingRepository(WorkLogDbContext db) : IOutcomeSettingRepository
{
    public async Task<IReadOnlyList<OutcomeSetting>> ListAsync(CancellationToken ct = default) => await db.OutcomeSettings.ToListAsync(ct);

    public void Add(OutcomeSetting setting) => db.OutcomeSettings.Add(setting);
}

internal sealed class WorkOrderRepository(WorkLogDbContext db) : Repository<WorkOrder>(db), IWorkOrderRepository
{
    public async Task<PagedResult<WorkOrder>> SearchAsync(WorkOrderFilter filter, CancellationToken ct = default)
    {
        var query = Set.AsQueryable();
        if (filter.From is { } from) query = query.Where(o => o.ScheduledDate >= from);
        if (filter.To is { } to) query = query.Where(o => o.ScheduledDate <= to);
        if (filter.AssigneeId is { } assignee) query = query.Where(o => o.AssigneeId == assignee);
        if (filter.WorkSiteId is { } site) query = query.Where(o => o.WorkSiteId == site);
        if (filter.Status is { } status) query = query.Where(o => o.Status == status);
        if (filter.Page.Search is { } search) query = query.Where(o => EF.Functions.Like(o.Number, $"%{search}%"));

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(o => o.ScheduledDate)
            .ThenBy(o => o.ScheduledTime)
            .ThenBy(o => o.Number)
            .Skip(filter.Page.Skip)
            .Take(filter.Page.PageSize)
            .ToListAsync(ct);

        return new PagedResult<WorkOrder>(items, filter.Page.Page, filter.Page.PageSize, total);
    }

    public Task<WorkOrder?> FindByNumberAsync(string number, CancellationToken ct = default) =>
        Set.FirstOrDefaultAsync(o => o.Number == number, ct);

    public async Task<IReadOnlyList<WorkOrder>> ListForAssigneeAsync(Guid assigneeId, DateOnly date, CancellationToken ct = default) =>
        await Set.Where(o => o.AssigneeId == assigneeId && o.ScheduledDate == date).ToListAsync(ct);

    public async Task<IReadOnlyDictionary<Guid, int>> CountPerSiteAsync(DateOnly from, DateOnly to, CancellationToken ct = default) =>
        await Set.Where(o => o.WorkSiteId != null && o.ScheduledDate >= from && o.ScheduledDate <= to && o.Status != WorkOrderStatus.Cancelled)
            .GroupBy(o => o.WorkSiteId!.Value)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

    public Task<bool> NumberExistsAsync(string number, Guid? exceptId, CancellationToken ct = default) =>
        Set.AnyAsync(o => o.Number == number && o.Id != exceptId, ct);
}

internal sealed class WorkdayRepository(WorkLogDbContext db) : Repository<Workday>(db), IWorkdayRepository
{
    private IQueryable<Workday> WithActivities => Set.Include(w => w.Activities).ThenInclude(a => a.Pauses).AsSplitQuery();

    public override Task<Workday?> FindAsync(Guid id, CancellationToken ct = default) =>
        WithActivities.FirstOrDefaultAsync(w => w.Id == id, ct);

    public Task<Workday?> FindOpenAsync(Guid memberId, CancellationToken ct = default) =>
        WithActivities.Where(w => w.MemberId == memberId && w.Status == WorkdayStatus.Open)
            .OrderByDescending(w => w.StartedAt)
            .FirstOrDefaultAsync(ct);

    public Task<Workday?> FindByDateAsync(Guid memberId, DateOnly date, CancellationToken ct = default) =>
        WithActivities.Where(w => w.MemberId == memberId && w.Date == date)
            .OrderByDescending(w => w.StartedAt)
            .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<Workday>> ListAsync(IReadOnlyCollection<Guid> memberIds, DateOnly from, DateOnly to, CancellationToken ct = default) =>
        memberIds.Count == 0
            ? []
            : await WithActivities.Where(w => memberIds.Contains(w.MemberId) && w.Date >= from && w.Date <= to).ToListAsync(ct);
}

internal sealed class LocationPointRepository(WorkLogDbContext db) : ILocationPointRepository
{
    public void Add(LocationPoint point) => db.LocationPoints.Add(point);

    public async Task<IReadOnlyList<LocationPoint>> ListByWorkdayAsync(Guid workdayId, CancellationToken ct = default) =>
        await db.LocationPoints.AsNoTracking().Where(p => p.WorkdayId == workdayId).OrderBy(p => p.CapturedAt).ToListAsync(ct);
}

internal sealed class EvidenceRepository(WorkLogDbContext db) : Repository<Evidence>(db), IEvidenceRepository
{
    public Task<Evidence?> FindByClientIdAsync(Guid clientId, CancellationToken ct = default) =>
        Set.FirstOrDefaultAsync(e => e.ClientId == clientId, ct);

    public async Task<IReadOnlyList<Evidence>> ListByWorkdayAsync(Guid workdayId, CancellationToken ct = default) =>
        await Set.Where(e => e.WorkdayId == workdayId).OrderBy(e => e.CapturedAt).ToListAsync(ct);
}

internal sealed class ParameterRepository(WorkLogDbContext db) : IParameterRepository
{
    public async Task<IReadOnlyList<ParameterValue>> ListAsync(IReadOnlyList<ParameterScope> chain, CancellationToken ct = default)
    {
        var country = chain.FirstOrDefault(s => s.Level == ParameterLevel.Country)?.Scope;
        var tenant = chain.FirstOrDefault(s => s.TenantId is not null)?.TenantId;
        var branch = chain.FirstOrDefault(s => s.Level == ParameterLevel.Branch)?.Scope;

        return await db.ParameterValues
            .Where(v => v.Level == ParameterLevel.Global
                        || (v.Level == ParameterLevel.Country && v.Scope == country)
                        || (v.Level == ParameterLevel.Company && v.TenantId == tenant && tenant != null)
                        || (v.Level == ParameterLevel.Branch && v.TenantId == tenant && v.Scope == branch && branch != null))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<ParameterValue>> ListAtScopeAsync(ParameterScope scope, ParameterKey key, CancellationToken ct = default) =>
        await db.ParameterValues
            .Where(v => v.Key == key && v.Level == scope.Level && v.TenantId == scope.TenantId && v.Scope == scope.Scope)
            .ToListAsync(ct);

    public void Add(ParameterValue value) => db.ParameterValues.Add(value);

    public void Remove(ParameterValue value) => db.ParameterValues.Remove(value);
}

internal sealed class PrivacyPolicyRepository(WorkLogDbContext db) : IPrivacyPolicyRepository
{
    public Task<PrivacyPolicyVersion?> CurrentAsync(DateOnly today, CancellationToken ct = default) =>
        db.PrivacyPolicies.AsNoTracking().Where(p => p.EffectiveFrom <= today).OrderByDescending(p => p.EffectiveFrom).FirstOrDefaultAsync(ct);
}

internal sealed class ProcessedEventRepository(WorkLogDbContext db) : IProcessedEventRepository
{
    public Task<ProcessedClientEvent?> FindAsync(Guid memberId, Guid clientEventId, CancellationToken ct = default) =>
        db.ProcessedClientEvents.AsNoTracking().FirstOrDefaultAsync(e => e.MemberId == memberId && e.ClientEventId == clientEventId, ct);

    public void Add(ProcessedClientEvent processed) => db.ProcessedClientEvents.Add(processed);
}

internal sealed class AuditStore(WorkLogDbContext db) : IAuditStore
{
    public void Add(AuditEvent auditEvent) => db.AuditEvents.Add(auditEvent);
}
