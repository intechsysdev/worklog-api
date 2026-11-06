using WorkLog.Domain.Common;
using WorkLog.Domain.Geo;

namespace WorkLog.Domain.Workdays;

/// <summary>Momento en que se tomó el punto. Solo se registra ubicación durante la jornada.</summary>
public enum LocationEvent
{
    WorkdayStart = 1,
    ActivityStart = 2,
    ActivityFinish = 3,
    Pause = 4,
    Resume = 5,
    Sample = 6,
    WorkdayClose = 7
}

/// <summary>
/// Punto de ubicación. Va fuera del agregado de jornada porque el muestreo genera muchos y no
/// participan en sus reglas. Consultarlos desde el panel queda en auditoría.
/// </summary>
public sealed class LocationPoint : Entity, ITenantOwned
{
    private LocationPoint()
    {
    }

    public Guid TenantId { get; private set; }
    public Guid MemberId { get; private set; }
    public Guid WorkdayId { get; private set; }
    public Guid? ActivityRecordId { get; private set; }
    public LocationEvent Event { get; private set; }
    public DateTimeOffset CapturedAt { get; private set; }
    public DateTimeOffset ReceivedAt { get; private set; }
    public double Latitude { get; private set; }
    public double Longitude { get; private set; }
    public double AccuracyMeters { get; private set; }
    public GeofenceStatus Geofence { get; private set; }

    public static LocationPoint Record(Guid tenantId, Guid memberId, Guid workdayId, Guid? activityRecordId,
        LocationEvent locationEvent, LocationReading reading, GeofenceStatus geofence, DateTimeOffset receivedAt) => new()
    {
        TenantId = tenantId,
        MemberId = memberId,
        WorkdayId = workdayId,
        ActivityRecordId = activityRecordId,
        Event = locationEvent,
        CapturedAt = reading.CapturedAt,
        ReceivedAt = receivedAt,
        Latitude = reading.Point.Latitude,
        Longitude = reading.Point.Longitude,
        AccuracyMeters = reading.AccuracyMeters,
        Geofence = geofence
    };
}

public enum EvidenceKind
{
    Photo = 1,
    Signature = 2
}

/// <summary>Foto o firma del cliente que respalda una actividad. El archivo vive en el almacenamiento.</summary>
public sealed class Evidence : TenantEntity
{
    public const long MaxSizeBytes = 10 * 1024 * 1024;

    public static readonly IReadOnlyList<string> AllowedContentTypes = ["image/jpeg", "image/png", "image/webp"];

    private Evidence()
    {
    }

    private Evidence(Guid tenantId) : base(tenantId)
    {
    }

    public Guid WorkdayId { get; private set; }
    public Guid ActivityRecordId { get; private set; }
    public Guid MemberId { get; private set; }
    public EvidenceKind Kind { get; private set; }
    public string StorageKey { get; private set; } = string.Empty;
    public string ContentType { get; private set; } = string.Empty;
    public long SizeBytes { get; private set; }
    public DateTimeOffset CapturedAt { get; private set; }

    /// <summary>Id que generó el teléfono: reintentar la subida no duplica la evidencia.</summary>
    public Guid ClientId { get; private set; }

    public static Result<Evidence> Create(Guid tenantId, Guid workdayId, Guid activityRecordId, Guid memberId,
        EvidenceKind kind, string storageKey, string contentType, long sizeBytes, DateTimeOffset capturedAt, Guid clientId)
    {
        if (!AllowedContentTypes.Contains(contentType.ToLowerInvariant()))
            return DomainError.Validation("evidence.type", "La evidencia debe ser una imagen JPG, PNG o WEBP.", "file");

        if (sizeBytes is <= 0 or > MaxSizeBytes)
            return DomainError.Validation("evidence.size", "La imagen supera el tamaño máximo de 10 MB.", "file");

        return new Evidence(tenantId)
        {
            WorkdayId = workdayId,
            ActivityRecordId = activityRecordId,
            MemberId = memberId,
            Kind = kind,
            StorageKey = storageKey,
            ContentType = contentType.ToLowerInvariant(),
            SizeBytes = sizeBytes,
            CapturedAt = capturedAt,
            ClientId = clientId
        };
    }
}
