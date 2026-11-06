using WorkLog.Api.Auth;
using WorkLog.Application.Abstractions;
using WorkLog.Application.Workdays;

namespace WorkLog.Api.Jobs;

/// <summary>
/// Adaptador de entrada por temporizador: cada pocos minutos recorre las empresas y ejecuta los
/// procesos automáticos (cierre de jornadas vencidas). Cada empresa corre en su propio alcance con
/// su contexto de empresa, igual que una petición: los filtros por tenant aplican igual.
/// </summary>
public sealed class WorkdayAutomationJob(IServiceScopeFactory scopes, ILogger<WorkdayAutomationJob> logger, IConfiguration configuration) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(1, configuration.GetValue("Automation:IntervalMinutes", 5)));
        if (!configuration.GetValue("Automation:Enabled", true))
        {
            logger.LogInformation("Procesos automáticos desactivados por configuración.");
            return;
        }

        using var timer = new PeriodicTimer(interval);
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Falló la ronda de procesos automáticos.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        IReadOnlyList<Guid> tenants;
        using (var scope = scopes.CreateScope())
            tenants = (await scope.ServiceProvider.GetRequiredService<ICompanyDirectory>().ListAsync(ct)).Select(c => c.Id).ToList();

        foreach (var tenantId in tenants)
        {
            using var scope = scopes.CreateScope();
            scope.ServiceProvider.GetRequiredService<RequestContext>().SetSystem(tenantId);

            var closed = await scope.ServiceProvider.GetRequiredService<IWorkdayAutomation>().CloseOverdueAsync(ct);
            if (closed > 0) logger.LogInformation("Cierre automático: {Count} jornada(s) en {Tenant}.", closed, tenantId);
        }
    }
}
