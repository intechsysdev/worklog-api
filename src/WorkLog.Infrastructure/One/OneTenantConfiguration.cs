using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;
using WorkLog.Application.Abstractions;

namespace WorkLog.Infrastructure.One;

/// <summary>
/// Configuración en ONE de la empresa de la petición, con la credencial que guardó WorkLog. Se
/// cachea unos minutos: Equipo hoy y Control de dispositivos la piden en cada carga, y sin caché
/// cada una sería una llamada a ONE antes de llegar a MobiControl.
/// </summary>
internal sealed class OneTenantConfiguration(
    ITenantContext tenant,
    ICompanyRepository companies,
    ISecretProtector protector,
    IOneIntegrationClient one,
    IMemoryCache cache) : ITenantConfiguration
{
    private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(5);

    /// <summary>Un fallo también se recuerda, poco, para no golpear a ONE en cada petición mientras se corrige.</summary>
    private static readonly TimeSpan FailureMemory = TimeSpan.FromSeconds(30);

    /// <summary>Una sola carga a la vez por empresa: las demás peticiones esperan y toman la caché.</summary>
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> Loads = new();

    private static string Key(Guid tenantId) => $"one:config:{tenantId:N}";

    public void Forget(Guid tenantId) => cache.Remove(Key(tenantId));

    public Task<OneConfigurationResult> GetAsync(CancellationToken ct = default) =>
        tenant.TenantId is { } tenantId
            ? ForTenantAsync(tenantId, ct)
            : Task.FromResult(OneConfigurationResult.Fail("No hay empresa en la petición."));

    public async Task<OneConfigurationResult> ForTenantAsync(Guid tenantId, CancellationToken ct = default)
    {
        if (cache.TryGetValue<OneConfigurationResult>(Key(tenantId), out var cached) && cached is not null)
            return cached;

        var load = Loads.GetOrAdd(tenantId, _ => new SemaphoreSlim(1, 1));
        await load.WaitAsync(ct);
        try
        {
            if (cache.TryGetValue(Key(tenantId), out cached) && cached is not null)
                return cached;

            var result = await LoadAsync(tenantId, ct);
            cache.Set(Key(tenantId), result, result.Configuration is null ? FailureMemory : Fresh);
            return result;
        }
        finally
        {
            load.Release();
        }
    }

    private async Task<OneConfigurationResult> LoadAsync(Guid tenantId, CancellationToken ct)
    {
        var company = await companies.FindAsync(tenantId, ct);
        if (company is not { HasOneCredential: true })
            return OneConfigurationResult.Fail(
                "WorkLog no tiene la credencial de ONE de esta empresa. Pégala en Control de dispositivos → Vínculo con INTECHSYS ONE.");

        var secret = protector.Unprotect(company.OneApiSecretProtected!);
        if (secret is null)
            return OneConfigurationResult.Fail("El servidor ya no puede leer la credencial guardada. Vuelve a pegarla.");

        return await one.GetConfigurationAsync(company.OneApiKey!, secret, tenantId, ct);
    }
}
