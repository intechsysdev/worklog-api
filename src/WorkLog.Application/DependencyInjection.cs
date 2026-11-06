using Microsoft.Extensions.DependencyInjection;
using WorkLog.Application.Abstractions;
using WorkLog.Application.Access;
using WorkLog.Application.Activities;
using WorkLog.Application.Auditing;
using WorkLog.Application.Organization;
using WorkLog.Application.Parameters;
using WorkLog.Application.Privacy;
using WorkLog.Application.Scheduling;
using WorkLog.Application.Workdays;

namespace WorkLog.Application;

public static class DependencyInjection
{
    /// <summary>Casos de uso (puertos de entrada) y servicios internos de la aplicación.</summary>
    public static IServiceCollection AddWorkLogApplication(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);

        services.AddScoped<IAuditTrail, AuditTrail>();
        services.AddScoped<IParameterProvider, ParameterProvider>();
        services.AddScoped<IScheduleCalculator, ScheduleCalculator>();

        services.AddScoped<IAccessService, AccessService>();
        services.AddScoped<IMemberService, MemberService>();
        services.AddScoped<IOrganizationService, OrganizationService>();
        services.AddScoped<IParameterService, ParameterService>();
        services.AddScoped<IScheduleService, ScheduleService>();
        services.AddScoped<ICatalogService, CatalogService>();
        services.AddScoped<IWorkOrderService, WorkOrderService>();
        services.AddScoped<IMyWorkdayService, MyWorkdayService>();
        services.AddScoped<ITeamService, TeamService>();
        services.AddScoped<ISyncService, SyncService>();
        services.AddScoped<IPrivacyService, PrivacyService>();

        // Fase 2
        services.AddScoped<Hours.IHoursEngine, Hours.HoursEngine>();
        services.AddScoped<Hours.IHoursService, Hours.HoursService>();
        services.AddScoped<Requests.IRequestService, Requests.RequestService>();
        services.AddScoped<Absences.IAbsenceService, Absences.AbsenceService>();
        services.AddScoped<Payroll.IPayrollService, Payroll.PayrollService>();
        services.AddScoped<IAuditService, AuditService>();
        services.AddScoped<Devices.IDeviceOrchestrator, Devices.DeviceOrchestrator>();
        services.AddScoped<Devices.IDeviceControlService, Devices.DeviceControlService>();
        services.AddScoped<IWorkdayAutomation, WorkdayAutomation>();

        return services;
    }
}
