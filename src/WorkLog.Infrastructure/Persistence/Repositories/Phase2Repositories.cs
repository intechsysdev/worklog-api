using Microsoft.EntityFrameworkCore;
using WorkLog.Application.Abstractions;
using WorkLog.Application.Common;
using WorkLog.Domain.Absences;
using WorkLog.Domain.Auditing;
using WorkLog.Domain.Devices;
using WorkLog.Domain.Organization;
using WorkLog.Domain.Payroll;
using WorkLog.Domain.Requests;
using WorkLog.Domain.Workdays;

namespace WorkLog.Infrastructure.Persistence.Repositories;

internal sealed class WorkRequestRepository(WorkLogDbContext db) : Repository<WorkRequest>(db), IWorkRequestRepository
{
    public async Task<PagedResult<WorkRequest>> SearchAsync(RequestFilter filter, CancellationToken ct = default)
    {
        var query = Set.AsQueryable();
        if (filter.MemberIds is { } ids) query = query.Where(r => ids.Contains(r.MemberId));
        if (filter.Type is { } type) query = query.Where(r => r.Type == type);
        if (filter.Status is { } status) query = query.Where(r => r.Status == status);
        if (filter.From is { } from) query = query.Where(r => r.Date >= from);
        if (filter.To is { } to) query = query.Where(r => r.Date <= to);

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderBy(r => r.Status != RequestStatus.Pending)
            .ThenByDescending(r => r.SubmittedAt)
            .Skip(filter.Page.Skip)
            .Take(filter.Page.PageSize)
            .ToListAsync(ct);

        return new PagedResult<WorkRequest>(items, filter.Page.Page, filter.Page.PageSize, total);
    }

    public async Task<IReadOnlyList<WorkRequest>> ListAsync(IReadOnlyCollection<Guid> memberIds, DateOnly from, DateOnly to, RequestStatus? status, CancellationToken ct = default) =>
        memberIds.Count == 0
            ? []
            : await Set.Where(r => memberIds.Contains(r.MemberId) && r.Date >= from && r.Date <= to && (status == null || r.Status == status))
                .ToListAsync(ct);

    public Task<int> CountPendingAsync(CancellationToken ct = default) => Set.CountAsync(r => r.Status == RequestStatus.Pending, ct);
}

internal sealed class AbsenceQueries(WorkLogDbContext db) : IAbsenceQueries
{
    public async Task<PagedResult<Absence>> SearchAsync(AbsenceFilter filter, CancellationToken ct = default)
    {
        var query = db.Absences.AsQueryable();
        if (filter.Type is { } type) query = query.Where(a => a.Type == type);
        if (filter.Status is { } status) query = query.Where(a => a.Status == status);
        if (filter.MemberId is { } member) query = query.Where(a => a.MemberId == member);
        if (filter.From is { } from) query = query.Where(a => a.To >= from);
        if (filter.To is { } to) query = query.Where(a => a.From <= to);

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderBy(a => a.Status != AbsenceStatus.Pending)
            .ThenByDescending(a => a.From)
            .Skip(filter.Page.Skip)
            .Take(filter.Page.PageSize)
            .ToListAsync(ct);

        return new PagedResult<Absence>(items, filter.Page.Page, filter.Page.PageSize, total);
    }

    public async Task<IReadOnlyList<Absence>> ListAsync(IReadOnlyCollection<Guid> memberIds, DateOnly from, DateOnly to, CancellationToken ct = default) =>
        memberIds.Count == 0
            ? []
            : await db.Absences.Where(a => memberIds.Contains(a.MemberId) && a.From <= to && a.To >= from).ToListAsync(ct);
}

internal sealed class PayrollPeriodRepository(WorkLogDbContext db) : IPayrollPeriodRepository
{
    public Task<PayrollPeriod?> FindAsync(Guid branchId, DateOnly from, CancellationToken ct = default) =>
        db.Set<PayrollPeriod>().Include(p => p.Lines).FirstOrDefaultAsync(p => p.BranchId == branchId && p.From == from, ct);

    public async Task<IReadOnlyList<PayrollPeriod>> ListAsync(Guid branchId, CancellationToken ct = default) =>
        await db.Set<PayrollPeriod>().Where(p => p.BranchId == branchId).ToListAsync(ct);

    public Task<bool> IsClosedAsync(Guid branchId, DateOnly date, CancellationToken ct = default) =>
        db.Set<PayrollPeriod>().AnyAsync(p => p.BranchId == branchId && p.Status == PayrollPeriodStatus.Closed && p.From <= date && p.To >= date, ct);

    public void Add(PayrollPeriod period) => db.Set<PayrollPeriod>().Add(period);
}

internal sealed class AuditQueries(WorkLogDbContext db) : IAuditQueries
{
    public async Task<PagedResult<AuditEvent>> SearchAsync(AuditFilter filter, CancellationToken ct = default)
    {
        var query = db.AuditEvents.AsNoTracking();
        if (filter.Origin is { } origin) query = query.Where(e => e.Origin == origin);
        if (!string.IsNullOrWhiteSpace(filter.EntityType)) query = query.Where(e => e.EntityType == filter.EntityType);
        if (filter.From is { } from) query = query.Where(e => e.OccurredAt >= from);
        if (filter.To is { } to) query = query.Where(e => e.OccurredAt < to);
        if (filter.Page.Search is { } search)
        {
            var term = $"%{search}%";
            query = query.Where(e => EF.Functions.Like(e.Summary, term) || EF.Functions.Like(e.ActorName, term));
        }

        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(e => e.OccurredAt).Skip(filter.Page.Skip).Take(filter.Page.PageSize).ToListAsync(ct);
        return new PagedResult<AuditEvent>(items, filter.Page.Page, filter.Page.PageSize, total);
    }

    public Task<AuditEvent?> FindAsync(Guid id, CancellationToken ct = default) =>
        db.AuditEvents.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct);

    public async Task<IReadOnlyList<string>> EntityTypesAsync(CancellationToken ct = default) =>
        await db.AuditEvents.Select(e => e.EntityType).Distinct().OrderBy(e => e).ToListAsync(ct);
}

internal sealed class DeviceControlRepository(WorkLogDbContext db) : IDeviceControlRepository
{
    public Task<DeviceControlSettings?> GetAsync(CancellationToken ct = default) => db.Set<DeviceControlSettings>().FirstOrDefaultAsync(ct);

    public void Add(DeviceControlSettings settings) => db.Set<DeviceControlSettings>().Add(settings);
}

internal sealed class DeviceCommandRepository(WorkLogDbContext db) : IDeviceCommandRepository
{
    public void Add(DeviceCommand command) => db.Set<DeviceCommand>().Add(command);

    public async Task<IReadOnlyDictionary<string, DeviceCommand>> LatestByDeviceAsync(CancellationToken ct = default)
    {
        var since = DateTimeOffset.UtcNow.AddDays(-7);
        var recent = await db.Set<DeviceCommand>().AsNoTracking().Where(c => c.RequestedAt >= since).ToListAsync(ct);
        return recent.GroupBy(c => c.DeviceId).ToDictionary(g => g.Key, g => g.MaxBy(c => c.RequestedAt)!);
    }
}

internal sealed class OpenWorkdayQueries(WorkLogDbContext db) : IOpenWorkdayQueries
{
    public async Task<IReadOnlyList<Workday>> ListOpenAsync(CancellationToken ct = default) =>
        await db.Workdays.Include(w => w.Activities).ThenInclude(a => a.Pauses).AsSplitQuery()
            .Where(w => w.Status == WorkdayStatus.Open)
            .ToListAsync(ct);
}

/// <summary>Listado sin filtro de empresa: lo usan los procesos automáticos para recorrerlas todas.</summary>
internal sealed class CompanyDirectory(WorkLogDbContext db) : ICompanyDirectory
{
    public async Task<IReadOnlyList<Company>> ListAsync(CancellationToken ct = default) =>
        await db.Companies.AsNoTracking().Where(c => c.IsProvisioned).ToListAsync(ct);
}
