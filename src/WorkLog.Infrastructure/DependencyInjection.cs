using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using WorkLog.Application.Abstractions;
using WorkLog.Infrastructure.One;
using WorkLog.Infrastructure.Persistence;
using WorkLog.Infrastructure.Persistence.Repositories;
using WorkLog.Infrastructure.Persistence.Seed;
using WorkLog.Infrastructure.Storage;

namespace WorkLog.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Adaptadores de salida: SQL Server, INTECHSYS ONE y almacenamiento de archivos.</summary>
    public static IServiceCollection AddWorkLogInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Default")
                               ?? throw new InvalidOperationException("Falta ConnectionStrings:Default.");

        services.AddScoped<AuditFieldsInterceptor>();
        services.AddDbContext<WorkLogDbContext>((sp, options) => options
            .UseSqlServer(connectionString, sql => sql
                .EnableRetryOnFailure(5, TimeSpan.FromSeconds(10), null)
                .CommandTimeout(60))
            .AddInterceptors(sp.GetRequiredService<AuditFieldsInterceptor>()));

        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<DbSeeder>();

        services.AddScoped<ICompanyRepository, CompanyRepository>();
        services.AddScoped<IBranchRepository, BranchRepository>();
        services.AddScoped<IWorkGroupRepository, WorkGroupRepository>();
        services.AddScoped<IWorkSiteRepository, WorkSiteRepository>();
        services.AddScoped<IRoleRepository, RoleRepository>();
        services.AddScoped<IMemberRepository, MemberRepository>();
        services.AddScoped<IScheduleTemplateRepository, ScheduleTemplateRepository>();
        services.AddScoped<IScheduleAssignmentRepository, ScheduleAssignmentRepository>();
        services.AddScoped<IShiftOverrideRepository, ShiftOverrideRepository>();
        services.AddScoped<IAbsenceRepository, AbsenceRepository>();
        services.AddScoped<IHolidayRepository, HolidayRepository>();
        services.AddScoped<IActivityCategoryRepository, ActivityCategoryRepository>();
        services.AddScoped<IActivityTypeRepository, ActivityTypeRepository>();
        services.AddScoped<IOutcomeSettingRepository, OutcomeSettingRepository>();
        services.AddScoped<IWorkOrderRepository, WorkOrderRepository>();
        services.AddScoped<IWorkdayRepository, WorkdayRepository>();
        services.AddScoped<ILocationPointRepository, LocationPointRepository>();
        services.AddScoped<IEvidenceRepository, EvidenceRepository>();
        services.AddScoped<IParameterRepository, ParameterRepository>();
        services.AddScoped<IPrivacyPolicyRepository, PrivacyPolicyRepository>();
        services.AddScoped<IProcessedEventRepository, ProcessedEventRepository>();
        services.AddScoped<IAuditStore, AuditStore>();

        // Fase 2
        services.AddScoped<IWorkRequestRepository, WorkRequestRepository>();
        services.AddScoped<IAbsenceQueries, AbsenceQueries>();
        services.AddScoped<IPayrollPeriodRepository, PayrollPeriodRepository>();
        services.AddScoped<IAuditQueries, AuditQueries>();
        services.AddScoped<IDeviceControlRepository, DeviceControlRepository>();
        services.AddScoped<IDeviceCommandRepository, DeviceCommandRepository>();
        services.AddScoped<IOpenWorkdayQueries, OpenWorkdayQueries>();
        services.AddScoped<ICompanyDirectory, CompanyDirectory>();
        services.AddSingleton<ISpreadsheetWriter, Files.ClosedXmlSpreadsheetWriter>();

        services.Configure<MobiControl.MobiControlOptions>(configuration.GetSection(MobiControl.MobiControlOptions.SectionName));
        services.AddScoped<IDeviceManagementGateway, MobiControl.SimulatedMobiControlGateway>();

        services.AddOptions<OneOptions>()
            .Bind(configuration.GetSection(OneOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddHttpClient<IIdentityProvider, OneIdentityProvider>((sp, http) =>
            {
                var one = sp.GetRequiredService<IOptions<OneOptions>>().Value;
                http.BaseAddress = new Uri(one.BaseUrl.TrimEnd('/') + "/");
                http.Timeout = TimeSpan.FromSeconds(one.TimeoutSeconds + 5);
            })
            .AddStandardResilienceHandler(resilience =>
            {
                resilience.AttemptTimeout.Timeout = TimeSpan.FromSeconds(5);
                resilience.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(12);
                resilience.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);
                resilience.Retry.MaxRetryAttempts = 2;
            });

        services.Configure<StorageOptions>(configuration.GetSection(StorageOptions.SectionName));
        if (string.Equals(configuration["Storage:Provider"], "AzureBlob", StringComparison.OrdinalIgnoreCase))
            services.AddSingleton<IFileStorage, AzureBlobFileStorage>();
        else
            services.AddSingleton<IFileStorage, LocalFileStorage>();

        return services;
    }
}
