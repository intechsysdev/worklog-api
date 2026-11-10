using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using WorkLog.Application.Abstractions;
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

namespace WorkLog.Infrastructure.Persistence;

/// <summary>
/// Contexto de WorkLog. Todo lo que pertenece a una empresa lleva un filtro global por la empresa
/// de la petición: una consulta sin empresa no devuelve nada, nunca datos de otra.
/// </summary>
public sealed class WorkLogDbContext(DbContextOptions<WorkLogDbContext> options, ITenantContext tenant) : DbContext(options)
{
    /// <summary>Lo lee el filtro global en cada consulta (EF lo parametriza, no lo fija al compilar el modelo).</summary>
    internal Guid CurrentTenantId => tenant.TenantId ?? Guid.Empty;

    public DbSet<Company> Companies => Set<Company>();
    public DbSet<Branch> Branches => Set<Branch>();
    public DbSet<WorkGroup> WorkGroups => Set<WorkGroup>();
    public DbSet<WorkSite> WorkSites => Set<WorkSite>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<Member> Members => Set<Member>();
    public DbSet<MobileSession> MobileSessions => Set<MobileSession>();
    public DbSet<ScheduleTemplate> ScheduleTemplates => Set<ScheduleTemplate>();
    public DbSet<ScheduleAssignment> ScheduleAssignments => Set<ScheduleAssignment>();
    public DbSet<ShiftOverride> ShiftOverrides => Set<ShiftOverride>();
    public DbSet<Holiday> Holidays => Set<Holiday>();
    public DbSet<Absence> Absences => Set<Absence>();
    public DbSet<ActivityCategory> ActivityCategories => Set<ActivityCategory>();
    public DbSet<ActivityType> ActivityTypes => Set<ActivityType>();
    public DbSet<OutcomeSetting> OutcomeSettings => Set<OutcomeSetting>();
    public DbSet<WorkOrder> WorkOrders => Set<WorkOrder>();
    public DbSet<Workday> Workdays => Set<Workday>();
    public DbSet<ActivityRecord> ActivityRecords => Set<ActivityRecord>();
    public DbSet<ActivityPause> ActivityPauses => Set<ActivityPause>();
    public DbSet<LocationPoint> LocationPoints => Set<LocationPoint>();
    public DbSet<Evidence> Evidences => Set<Evidence>();
    public DbSet<ParameterValue> ParameterValues => Set<ParameterValue>();
    public DbSet<PrivacyPolicyVersion> PrivacyPolicies => Set<PrivacyPolicyVersion>();
    public DbSet<ProcessedClientEvent> ProcessedClientEvents => Set<ProcessedClientEvent>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<Domain.Requests.WorkRequest> WorkRequests => Set<Domain.Requests.WorkRequest>();
    public DbSet<Domain.Payroll.PayrollPeriod> PayrollPeriods => Set<Domain.Payroll.PayrollPeriod>();
    public DbSet<Domain.Devices.DeviceControlSettings> DeviceControlSettings => Set<Domain.Devices.DeviceControlSettings>();
    public DbSet<Domain.Devices.DeviceCommand> DeviceCommands => Set<Domain.Devices.DeviceCommand>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(WorkLogDbContext).Assembly);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes().Where(t => !t.IsOwned()))
        {
            var clr = entityType.ClrType;

            // Los ids nacen en el dominio (Guid v7) o en el teléfono: la base nunca los genera.
            // Además, así EF reconoce como nuevo un hijo con id ya asignado (una actividad agregada
            // a una jornada existente) en vez de suponer que ya existía.
            if (typeof(Entity).IsAssignableFrom(clr))
                modelBuilder.Entity(clr).Property(nameof(Entity.Id)).ValueGeneratedNever();

            if (typeof(ITenantOwned).IsAssignableFrom(clr) && entityType.BaseType is null)
                modelBuilder.Entity(clr).HasQueryFilter(TenantFilter(clr));
        }
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Cadenas sin longitud explícita: 200 en vez de nvarchar(max), que no se puede indexar.
        configurationBuilder.Properties<string>().HaveMaxLength(200);
    }

    private LambdaExpression TenantFilter(Type clr)
    {
        var parameter = Expression.Parameter(clr, "e");
        var tenantId = Expression.Property(Expression.Convert(parameter, typeof(ITenantOwned)), nameof(ITenantOwned.TenantId));
        var current = Expression.Property(Expression.Constant(this), nameof(CurrentTenantId));
        return Expression.Lambda(Expression.Equal(tenantId, current), parameter);
    }
}
