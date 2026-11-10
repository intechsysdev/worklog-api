using WorkLog.Domain.Common;
using WorkLog.Domain.Geo;

namespace WorkLog.Domain.Organization;

/// <summary>Sitio de trabajo con geocerca: donde se valida que la persona está al iniciar y finalizar.</summary>
public sealed class WorkSite : TenantEntity
{
    private WorkSite()
    {
    }

    private WorkSite(Guid tenantId) : base(tenantId)
    {
    }

    public string Name { get; private set; } = string.Empty;
    public string NameKey { get; private set; } = string.Empty;
    public string? Address { get; private set; }
    public GeofenceShape Shape { get; private set; }
    public double CenterLatitude { get; private set; }
    public double CenterLongitude { get; private set; }
    public int RadiusMeters { get; private set; }
    public string? PolygonVertices { get; private set; }
    public bool IsActive { get; private set; } = true;

    /// <summary>Prefijo de las geocercas de WorkLog en MobiControl: la consola la comparte Geomonitoreo.</summary>
    public const string MobiControlPrefix = "WorkLog · ";

    /// <summary>MobiControl usa el nombre como llave de la ruta y lo limita a 100 caracteres.</summary>
    public const int MobiControlNameMaxLength = 100;

    /// <summary>Nombre con que la geocerca está hoy en MobiControl. Null: no está en la consola.</summary>
    public string? MobiControlName { get; private set; }

    public string? MobiControlReferenceId { get; private set; }
    public DateTimeOffset? MobiControlSyncedAt { get; private set; }

    /// <summary>Por qué no se pudo llevar a MobiControl la última vez. Null: está al día.</summary>
    public string? MobiControlError { get; private set; }

    /// <summary>Nombre que le corresponde en MobiControl: prefijo, sin los caracteres que rompen la ruta.</summary>
    public string ExpectedMobiControlName => MobiControlNameFor(Name);

    public static string MobiControlNameFor(string siteName)
    {
        var safe = string.Join(' ', siteName.Split(['/', '\\', '?', '#', '%'], StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(part => part.Split(' ', StringSplitOptions.RemoveEmptyEntries)));
        var full = MobiControlPrefix + safe;
        return full.Length <= MobiControlNameMaxLength ? full : full[..MobiControlNameMaxLength].TrimEnd();
    }

    public void MarkInMobiControl(string name, string? referenceId, DateTimeOffset at)
    {
        MobiControlName = name;
        MobiControlReferenceId = referenceId;
        MobiControlSyncedAt = at;
        MobiControlError = null;
    }

    /// <summary>Ya no está en la consola (se borró o nunca se creó). Con <paramref name="error"/>, queda pendiente.</summary>
    public void MarkOutOfMobiControl(string? error = null)
    {
        MobiControlName = null;
        MobiControlReferenceId = null;
        MobiControlSyncedAt = null;
        MobiControlError = error;
    }

    public void MarkMobiControlError(string error) => MobiControlError = error.Length > 300 ? error[..300] : error;

    public Geofence Geofence => Geofence.Restore(Shape, CenterLatitude, CenterLongitude, RadiusMeters, PolygonVertices);

    public static Result<WorkSite> Create(Guid tenantId, string name, string? address, Geofence geofence)
    {
        var site = new WorkSite(tenantId);
        var applied = site.Update(name, address, geofence);
        return applied.IsSuccess ? site : applied.Error!;
    }

    public Result Update(string name, string? address, Geofence geofence)
    {
        var cleaned = Text.Clean(name);
        if (Guard.Name(cleaned, "name", 2, 120, "El nombre") is { } nameError) return nameError;

        var cleanedAddress = Text.CleanOrNull(address);
        if (Guard.MaxLength(cleanedAddress, "address", 250, "La dirección") is { } addressError) return addressError;

        Name = cleaned;
        NameKey = Text.Key(cleaned);
        Address = cleanedAddress;
        Shape = geofence.Shape;
        CenterLatitude = geofence.Center.Latitude;
        CenterLongitude = geofence.Center.Longitude;
        RadiusMeters = geofence.RadiusMeters;
        PolygonVertices = geofence.EncodeVertices();
        return Result.Success();
    }

    public void Deactivate() => IsActive = false;

    public void Activate() => IsActive = true;
}
