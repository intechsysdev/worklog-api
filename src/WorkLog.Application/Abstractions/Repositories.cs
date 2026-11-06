using WorkLog.Application.Common;
using WorkLog.Domain.Absences;
using WorkLog.Domain.Access;
using WorkLog.Domain.Activities;
using WorkLog.Domain.Common;
using WorkLog.Domain.Organization;
using WorkLog.Domain.Parameters;
using WorkLog.Domain.Privacy;
using WorkLog.Domain.Scheduling;
using WorkLog.Domain.Sync;
using WorkLog.Domain.Workdays;

namespace WorkLog.Application.Abstractions;

/// <summary>
/// Repositorio de un agregado. Todo lo que pertenece a una empresa ya viene filtrado por la
/// empresa de la petición: los casos de uso nunca filtran por tenant a mano.
/// </summary>
public interface IRepository<T> where T : Entity
{
    Task<T?> FindAsync(Guid id, CancellationToken ct = default);

    Task<IReadOnlyList<T>> FindManyAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default);

    void Add(T entity);

    void Remove(T entity);
}

public interface ICompanyRepository
{
    Task<Company?> FindAsync(Guid tenantId, CancellationToken ct = default);

    void Add(Company company);
}

public interface IBranchRepository : IRepository<Branch>
{
    Task<IReadOnlyList<Branch>> ListAsync(CancellationToken ct = default);

    Task<bool> NameExistsAsync(string nameKey, Guid? exceptId, CancellationToken ct = default);
}

public interface IWorkGroupRepository : IRepository<WorkGroup>
{
    Task<IReadOnlyList<WorkGroup>> ListAsync(Guid? branchId = null, CancellationToken ct = default);

    Task<bool> NameExistsAsync(string nameKey, Guid branchId, Guid? exceptId, CancellationToken ct = default);
}

public interface IWorkSiteRepository : IRepository<WorkSite>
{
    Task<IReadOnlyList<WorkSite>> ListAsync(bool includeInactive = false, CancellationToken ct = default);

    Task<bool> NameExistsAsync(string nameKey, Guid? exceptId, CancellationToken ct = default);
}

public interface IRoleRepository : IRepository<Role>
{
    Task<IReadOnlyList<Role>> ListAsync(CancellationToken ct = default);

    Task<Role?> FindBySystemKeyAsync(string systemKey, CancellationToken ct = default);

    Task<bool> NameExistsAsync(string nameKey, Guid? exceptId, CancellationToken ct = default);
}

public sealed record MemberFilter(
    PageRequest Page,
    Guid? BranchId = null,
    Guid? WorkGroupId = null,
    Guid? RoleId = null,
    MemberStatus? Status = null,
    MemberUsage? Usage = null);

public interface IMemberRepository : IRepository<Member>
{
    Task<Member?> FindByOneUserAsync(Guid oneUserId, CancellationToken ct = default);

    Task<Member?> FindByEmailAsync(string normalizedEmail, CancellationToken ct = default);

    Task<PagedResult<Member>> SearchAsync(MemberFilter filter, CancellationToken ct = default);

    /// <summary>Personas activas o invitadas que usan la app, opcionalmente de una sede.</summary>
    Task<IReadOnlyList<Member>> ListMobileAsync(Guid? branchId, CancellationToken ct = default);

    Task<IReadOnlyList<Member>> ListAllAsync(CancellationToken ct = default);

    Task<int> CountByBranchAsync(Guid branchId, CancellationToken ct = default);

    Task<int> CountByGroupAsync(Guid groupId, CancellationToken ct = default);

    Task<IReadOnlyDictionary<Guid, int>> CountPerRoleAsync(CancellationToken ct = default);
}

public interface IScheduleTemplateRepository : IRepository<ScheduleTemplate>
{
    Task<IReadOnlyList<ScheduleTemplate>> ListAsync(CancellationToken ct = default);

    Task<bool> NameExistsAsync(string nameKey, Guid? exceptId, CancellationToken ct = default);
}

public interface IScheduleAssignmentRepository : IRepository<ScheduleAssignment>
{
    Task<IReadOnlyList<ScheduleAssignment>> ListAsync(CancellationToken ct = default);

    Task<bool> ExistsAsync(AssignmentLevel level, Guid targetId, DateOnly effectiveFrom, CancellationToken ct = default);
}

public interface IShiftOverrideRepository : IRepository<ShiftOverride>
{
    Task<IReadOnlyList<ShiftOverride>> ListAsync(IReadOnlyCollection<Guid> memberIds, DateOnly from, DateOnly to, CancellationToken ct = default);
}

public interface IAbsenceRepository : IRepository<Absence>
{
    /// <summary>Novedades aprobadas que tocan el rango.</summary>
    Task<IReadOnlyList<Absence>> ListApprovedAsync(IReadOnlyCollection<Guid> memberIds, DateOnly from, DateOnly to, CancellationToken ct = default);
}

public interface IHolidayRepository
{
    Task<IReadOnlyList<Holiday>> ListAsync(string countryCode, DateOnly from, DateOnly to, CancellationToken ct = default);
}

public interface IActivityCategoryRepository : IRepository<ActivityCategory>
{
    Task<IReadOnlyList<ActivityCategory>> ListAsync(CancellationToken ct = default);

    Task<bool> NameExistsAsync(string nameKey, Guid? exceptId, CancellationToken ct = default);
}

public interface IActivityTypeRepository : IRepository<ActivityType>
{
    Task<IReadOnlyList<ActivityType>> ListAsync(bool activeOnly = false, CancellationToken ct = default);

    Task<bool> NameExistsAsync(string nameKey, Guid? exceptId, CancellationToken ct = default);
}

public interface IOutcomeSettingRepository
{
    Task<IReadOnlyList<OutcomeSetting>> ListAsync(CancellationToken ct = default);

    void Add(OutcomeSetting setting);
}

public sealed record WorkOrderFilter(
    PageRequest Page,
    DateOnly? From = null,
    DateOnly? To = null,
    Guid? AssigneeId = null,
    Guid? WorkSiteId = null,
    WorkOrderStatus? Status = null);

public interface IWorkOrderRepository : IRepository<WorkOrder>
{
    Task<PagedResult<WorkOrder>> SearchAsync(WorkOrderFilter filter, CancellationToken ct = default);

    Task<WorkOrder?> FindByNumberAsync(string number, CancellationToken ct = default);

    /// <summary>OT de una persona para un día (incluye las ya cerradas, para mostrarlas como hechas).</summary>
    Task<IReadOnlyList<WorkOrder>> ListForAssigneeAsync(Guid assigneeId, DateOnly date, CancellationToken ct = default);

    Task<IReadOnlyDictionary<Guid, int>> CountPerSiteAsync(DateOnly from, DateOnly to, CancellationToken ct = default);

    Task<bool> NumberExistsAsync(string number, Guid? exceptId, CancellationToken ct = default);
}

public interface IWorkdayRepository : IRepository<Workday>
{
    /// <summary>Jornada abierta de la persona, con sus actividades y pausas.</summary>
    Task<Workday?> FindOpenAsync(Guid memberId, CancellationToken ct = default);

    /// <summary>Jornada de un día (la más reciente si hubo varias), con sus actividades y pausas.</summary>
    Task<Workday?> FindByDateAsync(Guid memberId, DateOnly date, CancellationToken ct = default);

    Task<IReadOnlyList<Workday>> ListAsync(IReadOnlyCollection<Guid> memberIds, DateOnly from, DateOnly to, CancellationToken ct = default);
}

public interface ILocationPointRepository
{
    void Add(LocationPoint point);

    Task<IReadOnlyList<LocationPoint>> ListByWorkdayAsync(Guid workdayId, CancellationToken ct = default);
}

public interface IEvidenceRepository : IRepository<Evidence>
{
    Task<Evidence?> FindByClientIdAsync(Guid clientId, CancellationToken ct = default);

    Task<IReadOnlyList<Evidence>> ListByWorkdayAsync(Guid workdayId, CancellationToken ct = default);
}

public interface IParameterRepository
{
    /// <summary>Valores guardados para los niveles de la cadena (Global, País, Empresa, Sede).</summary>
    Task<IReadOnlyList<ParameterValue>> ListAsync(IReadOnlyList<ParameterScope> chain, CancellationToken ct = default);

    Task<IReadOnlyList<ParameterValue>> ListAtScopeAsync(ParameterScope scope, ParameterKey key, CancellationToken ct = default);

    void Add(ParameterValue value);

    void Remove(ParameterValue value);
}

public interface IPrivacyPolicyRepository
{
    Task<PrivacyPolicyVersion?> CurrentAsync(DateOnly today, CancellationToken ct = default);
}

public interface IProcessedEventRepository
{
    Task<ProcessedClientEvent?> FindAsync(Guid memberId, Guid clientEventId, CancellationToken ct = default);

    void Add(ProcessedClientEvent processed);
}
