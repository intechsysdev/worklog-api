using WorkLog.Application.Abstractions;
using WorkLog.Domain.Geo;
using WorkLog.Domain.Organization;

namespace WorkLog.Application.Organization;

/// <summary>Resultado de una llamada de geocercas a MobiControl.</summary>
public sealed record GeofenceCallResult(bool Succeeded, string? ReferenceId, string? Problem, bool AlreadyExists = false)
{
    public static GeofenceCallResult Ok(string? referenceId = null) => new(true, referenceId, null);

    public static GeofenceCallResult Fail(string problem, bool alreadyExists = false) => new(false, null, problem, alreadyExists);
}

/// <summary>
/// Geocercas en la consola de MobiControl de la empresa. El nombre es la llave en la consola; la
/// forma no se puede editar (para cambiarla se borra y se crea de nuevo).
/// </summary>
public interface IGeofenceGateway
{
    /// <summary>Null si la consola está disponible; si no, por qué (sin vínculo con ONE, sin variables, etc.).</summary>
    Task<string?> UnavailableReasonAsync(CancellationToken ct = default);

    Task<GeofenceCallResult> CreateAsync(string name, IReadOnlyList<GeoPoint> ring, CancellationToken ct = default);

    Task<GeofenceCallResult> RenameAsync(string currentName, string newName, CancellationToken ct = default);

    /// <summary>Si ya no existe en la consola, también es éxito.</summary>
    Task<GeofenceCallResult> DeleteAsync(string name, CancellationToken ct = default);
}

/// <summary>
/// Lleva cada sitio de WorkLog a MobiControl. WorkLog es dueño de sus geocercas (prefijo
/// <see cref="WorkSite.MobiControlPrefix"/>) y no toca las demás de la consola. Un fallo de la consola
/// no impide guardar el sitio: queda pendiente con el motivo y se reintenta con «Sincronizar».
/// </summary>
public interface ISiteGeofenceSync
{
    /// <param name="shapeChanged">La forma cambió: en la consola hay que borrarla y crearla de nuevo.</param>
    Task SyncAsync(WorkSite site, bool shapeChanged, CancellationToken ct = default);
}

internal sealed class SiteGeofenceSync(IGeofenceGateway gateway, TimeProvider clock) : ISiteGeofenceSync
{
    public async Task SyncAsync(WorkSite site, bool shapeChanged, CancellationToken ct = default)
    {
        if (await gateway.UnavailableReasonAsync(ct) is { } unavailable)
        {
            site.MarkMobiControlError(unavailable);
            return;
        }

        // Inactivo: no debe quedar en la consola (ninguna regla debe usar un sitio que ya no se usa).
        if (!site.IsActive)
        {
            if (site.MobiControlName is not { } current)
            {
                site.MarkOutOfMobiControl();
                return;
            }

            var deleted = await gateway.DeleteAsync(current, ct);
            if (deleted.Succeeded) site.MarkOutOfMobiControl();
            else site.MarkMobiControlError(deleted.Problem!);
            return;
        }

        var expected = site.ExpectedMobiControlName;
        if (site.MobiControlName is not { } existing)
        {
            await CreateAsync(site, expected, ct);
            return;
        }

        if (shapeChanged)
        {
            var removed = await gateway.DeleteAsync(existing, ct);
            if (!removed.Succeeded)
            {
                site.MarkMobiControlError(removed.Problem!);
                return;
            }

            site.MarkOutOfMobiControl();
            await CreateAsync(site, expected, ct);
            return;
        }

        if (existing != expected)
        {
            var renamed = await gateway.RenameAsync(existing, expected, ct);
            if (renamed.Succeeded) site.MarkInMobiControl(expected, renamed.ReferenceId ?? site.MobiControlReferenceId, clock.GetUtcNow());
            else site.MarkMobiControlError(renamed.Problem!);
            return;
        }

        // Al día: solo se limpia un error viejo (por ejemplo, la consola volvió a responder).
        if (site.MobiControlError is not null)
            site.MarkInMobiControl(existing, site.MobiControlReferenceId, site.MobiControlSyncedAt ?? clock.GetUtcNow());
    }

    /// <summary>
    /// Crea la geocerca. Si ya hay una con ese nombre es de WorkLog (por el prefijo): quedó de un intento
    /// anterior, así que se reemplaza para que la forma sea la del sitio.
    /// </summary>
    private async Task CreateAsync(WorkSite site, string name, CancellationToken ct)
    {
        var ring = site.Geofence.Ring();
        var created = await gateway.CreateAsync(name, ring, ct);
        if (!created.Succeeded && created.AlreadyExists && (await gateway.DeleteAsync(name, ct)).Succeeded)
            created = await gateway.CreateAsync(name, ring, ct);

        if (created.Succeeded) site.MarkInMobiControl(name, created.ReferenceId, clock.GetUtcNow());
        else site.MarkOutOfMobiControl(created.Problem);
    }
}
