using System.Globalization;
using WorkLog.Domain.Common;

namespace WorkLog.Domain.Geo;

/// <summary>Coordenada WGS84 en grados decimales.</summary>
public readonly record struct GeoPoint
{
    private const double EarthRadiusMeters = 6_371_008.8;

    private GeoPoint(double latitude, double longitude)
    {
        Latitude = latitude;
        Longitude = longitude;
    }

    public double Latitude { get; }
    public double Longitude { get; }

    public static Result<GeoPoint> Create(double latitude, double longitude)
    {
        if (double.IsNaN(latitude) || latitude is < -90 or > 90)
            return DomainError.Validation("geo.latitude", "La latitud debe estar entre -90 y 90.", "latitude");

        if (double.IsNaN(longitude) || longitude is < -180 or > 180)
            return DomainError.Validation("geo.longitude", "La longitud debe estar entre -180 y 180.", "longitude");

        return new GeoPoint(latitude, longitude);
    }

    /// <summary>Distancia sobre la esfera (Haversine), en metros. Suficiente para geocercas urbanas.</summary>
    public double DistanceTo(GeoPoint other)
    {
        var dLat = ToRadians(other.Latitude - Latitude);
        var dLon = ToRadians(other.Longitude - Longitude);
        var a = Math.Pow(Math.Sin(dLat / 2), 2) +
                Math.Cos(ToRadians(Latitude)) * Math.Cos(ToRadians(other.Latitude)) * Math.Pow(Math.Sin(dLon / 2), 2);
        return 2 * EarthRadiusMeters * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Latitude:0.######},{Longitude:0.######}");

    private static double ToRadians(double degrees) => degrees * Math.PI / 180d;
}

/// <summary>Lectura del GPS del teléfono: punto, precisión declarada y hora de captura.</summary>
public sealed record LocationReading(GeoPoint Point, double AccuracyMeters, DateTimeOffset CapturedAt)
{
    public static Result<LocationReading> Create(double latitude, double longitude, double accuracyMeters, DateTimeOffset capturedAt)
    {
        var point = GeoPoint.Create(latitude, longitude);
        if (point.IsFailure) return point.Error!;

        if (double.IsNaN(accuracyMeters) || accuracyMeters < 0 || accuracyMeters > 100_000)
            return DomainError.Validation("geo.accuracy", "La precisión del GPS no es válida.", "accuracy");

        return new LocationReading(point.Value, Math.Round(accuracyMeters, 1), capturedAt);
    }
}

/// <summary>Resultado de comparar una lectura con la geocerca del sitio.</summary>
public enum GeofenceStatus
{
    /// <summary>La actividad no tiene sitio con geocerca.</summary>
    NotApplicable = 0,
    Inside = 1,
    Outside = 2,

    /// <summary>La precisión fue peor que el mínimo: el punto se guarda pero no valida.</summary>
    Unknown = 3,

    /// <summary>No llegó ubicación (permiso negado o GPS apagado).</summary>
    NoLocation = 4
}
