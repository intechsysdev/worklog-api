using WorkLog.Application.Organization;
using WorkLog.Domain.Geo;

namespace WorkLog.Infrastructure.MobiControl;

/// <summary>Geocercas sin consola (MobiControl:Mode distinto de Rest): los sitios quedan solo en WorkLog.</summary>
internal sealed class UnavailableGeofenceGateway : IGeofenceGateway
{
    private const string Reason = "MobiControl no está conectado en este entorno.";

    public Task<string?> UnavailableReasonAsync(CancellationToken ct = default) => Task.FromResult<string?>(Reason);

    public Task<GeofenceCallResult> CreateAsync(string name, IReadOnlyList<GeoPoint> ring, CancellationToken ct = default) =>
        Task.FromResult(GeofenceCallResult.Fail(Reason));

    public Task<GeofenceCallResult> RenameAsync(string currentName, string newName, CancellationToken ct = default) =>
        Task.FromResult(GeofenceCallResult.Fail(Reason));

    public Task<GeofenceCallResult> DeleteAsync(string name, CancellationToken ct = default) => Task.FromResult(GeofenceCallResult.Fail(Reason));
}
