using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using WorkLog.Application.Abstractions;
using WorkLog.Domain.Devices;

namespace WorkLog.Infrastructure.MobiControl;

public sealed class MobiControlOptions
{
    public const string SectionName = "MobiControl";

    public const string SimulatorMode = "Simulator";
    public const string RestMode = "Rest";

    /// <summary>
    /// "Rest": la consola real de cada empresa, con sus variables de ONE (appsettings.json).
    /// "Simulator": solo para desarrollo (appsettings.Development.json). Cualquier otro valor,
    /// o ninguno, deja a WorkLog sin MobiControl.
    /// </summary>
    public string Mode { get; set; } = "Disabled";

    /// <summary>Códigos de verificación que el simulador reporta como fallidos, para probar la pantalla.</summary>
    public string[] FailingChecks { get; set; } = [];
}

/// <summary>
/// Simulador de SOTI MobiControl: una flota estable por empresa (mismos equipos en cada arranque)
/// con estado en memoria. Sirve para usar el panel y la app completos mientras no haya consola real.
/// El adaptador real vendrá de geomonitoreo-api (ClienteMobiControl), con las credenciales que cada
/// empresa configura en ONE.
/// </summary>
internal sealed class SimulatedMobiControlGateway(ITenantContext tenant, TimeProvider clock, IOptions<MobiControlOptions> options) : IDeviceManagementGateway
{
    private static readonly ConcurrentDictionary<Guid, Fleet> Fleets = new();

    private static readonly string[] Models = ["Zebra TC26", "Samsung Galaxy XCover7", "Zebra TC53", "Samsung Galaxy Tab Active5"];
    private static readonly string[] Groups = ["INTECHSYS / Campo / Bogotá Norte", "INTECHSYS / Campo / Bogotá Sur", "INTECHSYS / Campo / Medellín", "INTECHSYS / Compartidos / Bogotá Norte"];

    private static readonly string[] Apps =
    [
        "com.intechsys.worklog", "com.android.dialer", "com.empresa.mail", "com.empresa.ot", "com.empresa.inventario",
        "com.empresa.chat", "com.android.camera", "com.google.android.apps.maps", "com.android.chrome"
    ];

    public bool CanControlWorkday => true;

    public Task<IReadOnlyList<ManagedDevice>> ListDevicesAsync(CancellationToken ct = default)
    {
        var fleet = Current();
        var now = clock.GetUtcNow();
        IReadOnlyList<ManagedDevice> devices = fleet.Devices.Values.Select(d => d.Snapshot(now)).OrderBy(d => d.Serial).ToList();
        return Task.FromResult(devices);
    }

    public Task<IReadOnlyList<VerificationCheck>> VerifyAsync(CancellationToken ct = default)
    {
        var failing = options.Value.FailingChecks.ToHashSet(StringComparer.OrdinalIgnoreCase);
        VerificationCheck Check(string code, string name, string ok, string fail) =>
            new(code, name, !failing.Contains(code), failing.Contains(code) ? fail : ok);

        IReadOnlyList<VerificationCheck> checks =
        [
            Check("connection", "Conexión con MobiControl", "Configurada en INTECHSYS ONE · simulador 2026.0", "No se pudo conectar con la consola."),
            Check("profile_off", "Perfil «WorkLog · Fuera de jornada»", "Lockdown con WorkLog y Teléfono.", "No existe el perfil en la consola."),
            Check("profile_on", "Perfil «WorkLog · En jornada»", "Lockdown con WorkLog y las apps corporativas.", "No existe el perfil en la consola."),
            Check("attribute", "Atributo WL_JORNADA y filtros", "Valores ABIERTA / CERRADA con sus filtros de asignación.", "Falta el atributo personalizado WL_JORNADA."),
            Check("shared_device", "Shared Device abre WorkLog al ingresar", "WorkLog es la app de inicio de Shared Device.", "Shared Device no abre WorkLog al ingresar.")
        ];
        return Task.FromResult(checks);
    }

    public Task<IReadOnlyList<string>> ListAppsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>(Apps);

    public Task<GatewayResult> SetWorkdayStateAsync(string deviceId, bool open, CancellationToken ct = default)
    {
        if (!Current().Devices.TryGetValue(deviceId, out var device))
            return Task.FromResult(new GatewayResult(DeviceCommandStatus.Failed, "El equipo no está inscrito."));

        device.WorkdayOpen = open;
        return Task.FromResult(new GatewayResult(device.Online(clock.GetUtcNow()) ? DeviceCommandStatus.Applied : DeviceCommandStatus.Queued, null));
    }

    public Task<GatewayResult> LogoutSharedAsync(string deviceId, CancellationToken ct = default) =>
        Task.FromResult(Current().Devices.ContainsKey(deviceId)
            ? new GatewayResult(DeviceCommandStatus.Applied, null)
            : new GatewayResult(DeviceCommandStatus.Failed, "El equipo no está inscrito."));

    private Fleet Current() => Fleets.GetOrAdd(tenant.TenantId ?? Guid.Empty, id => new Fleet(id, clock.GetUtcNow()));

    private sealed class Fleet
    {
        public Fleet(Guid tenantId, DateTimeOffset now)
        {
            var seed = SHA256.HashData(Encoding.UTF8.GetBytes(tenantId.ToString()));
            for (var i = 0; i < 8; i++)
            {
                var serial = $"R{Convert.ToHexString(seed, i * 2, 2)}{i:00}X{seed[i] % 90 + 10}";
                var shared = i >= 6;
                Devices[$"mc-{serial.ToLowerInvariant()}"] = new SimDevice(
                    $"mc-{serial.ToLowerInvariant()}", Models[i % Models.Length], serial,
                    shared ? Groups[3] : Groups[i % 3], shared, seed[i + 8], now.AddDays(-(seed[i] % 20) - 1));
            }
        }

        public ConcurrentDictionary<string, SimDevice> Devices { get; } = new();
    }

    private sealed class SimDevice(string id, string model, string serial, string group, bool shared, byte seed, DateTimeOffset enrolled)
    {
        public bool WorkdayOpen { get; set; }

        /// <summary>Uno de cada ocho equipos aparece sin conexión, para ver ese caso en el panel.</summary>
        public bool Online(DateTimeOffset now) => seed % 8 != 0;

        public ManagedDevice Snapshot(DateTimeOffset now)
        {
            // La batería baja durante el día y "se carga" de noche: varía sin ser aleatoria.
            var battery = Math.Clamp(100 - (int)((now.ToUnixTimeSeconds() / 60 + seed * 7) % 95), 5, 100);
            var online = Online(now);
            return new ManagedDevice(id, model, serial, group, shared, online, battery,
                online ? now.AddMinutes(-(seed % 5)) : now.AddHours(-(seed % 30) - 2), enrolled);
        }
    }
}
