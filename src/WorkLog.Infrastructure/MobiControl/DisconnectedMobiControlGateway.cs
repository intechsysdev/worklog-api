using WorkLog.Application.Abstractions;
using WorkLog.Domain.Devices;

namespace WorkLog.Infrastructure.MobiControl;

/// <summary>
/// MobiControl apagado (MobiControl:Mode distinto de Rest y de Simulator). No inventa equipos ni
/// aprueba la verificación, para que nadie active el control de dispositivos creyendo que la
/// consola responde.
/// </summary>
internal sealed class DisconnectedMobiControlGateway : IDeviceManagementGateway
{
    private const string NotConnected = "WorkLog todavía no está conectado con la consola de MobiControl de esta empresa.";

    public bool CanControlWorkday => false;

    public Task<IReadOnlyList<ManagedDevice>> ListDevicesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ManagedDevice>>([]);

    public Task<IReadOnlyList<VerificationCheck>> VerifyAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<VerificationCheck>>([new("connection", "Conexión con MobiControl", false, NotConnected)]);

    public Task<IReadOnlyList<string>> ListAppsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    public Task<GatewayResult> SetWorkdayStateAsync(string deviceId, bool open, CancellationToken ct = default) =>
        Task.FromResult(new GatewayResult(DeviceCommandStatus.Failed, NotConnected));

    public Task<GatewayResult> LogoutSharedAsync(string deviceId, CancellationToken ct = default) =>
        Task.FromResult(new GatewayResult(DeviceCommandStatus.Failed, NotConnected));
}
