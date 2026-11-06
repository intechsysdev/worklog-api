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
