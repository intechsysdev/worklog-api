using System.Globalization;
using WorkLog.Domain.Common;

namespace WorkLog.Domain.Geo;

public enum GeofenceShape
{
    Circle = 1,
    Polygon = 2
}

/// <summary>Zona de un sitio de trabajo: círculo con radio o polígono de al menos tres esquinas.</summary>
public sealed class Geofence
{
    public const int MinRadiusMeters = 30;
    public const int MaxRadiusMeters = 500;
    public const int MaxVertices = 50;

    private Geofence(GeofenceShape shape, GeoPoint center, int radiusMeters, IReadOnlyList<GeoPoint> vertices)
    {
        Shape = shape;
        Center = center;
        RadiusMeters = radiusMeters;
        Vertices = vertices;
    }

    public GeofenceShape Shape { get; }
    public GeoPoint Center { get; }

    /// <summary>Radio del círculo; en un polígono, la distancia del centro a la esquina más lejana.</summary>
    public int RadiusMeters { get; }

    public IReadOnlyList<GeoPoint> Vertices { get; }

    public static Result<Geofence> Circle(GeoPoint center, int radiusMeters)
    {
        if (Guard.Range(radiusMeters, MinRadiusMeters, MaxRadiusMeters, "radiusMeters", "El radio") is { } error)
            return error;

        return new Geofence(GeofenceShape.Circle, center, radiusMeters, []);
    }

    public static Result<Geofence> Polygon(IReadOnlyList<GeoPoint> vertices)
    {
        if (vertices.Count < 3)
            return DomainError.Validation("geofence.vertices", "El polígono necesita al menos tres esquinas.", "vertices");

        if (vertices.Count > MaxVertices)
            return DomainError.Validation("geofence.vertices", $"El polígono admite máximo {MaxVertices} esquinas.", "vertices");

        var center = GeoPoint.Create(vertices.Average(v => v.Latitude), vertices.Average(v => v.Longitude)).Value;
        var radius = (int)Math.Ceiling(vertices.Max(v => center.DistanceTo(v)));
        return new Geofence(GeofenceShape.Polygon, center, radius, vertices.ToArray());
    }

    /// <summary>Reconstruye la geocerca desde sus columnas persistidas.</summary>
    public static Geofence Restore(GeofenceShape shape, double centerLatitude, double centerLongitude, int radiusMeters, string? encodedVertices)
    {
        var center = GeoPoint.Create(centerLatitude, centerLongitude).Value;
        return shape == GeofenceShape.Polygon
            ? new Geofence(shape, center, radiusMeters, Decode(encodedVertices))
            : new Geofence(shape, center, radiusMeters, []);
    }

    /// <summary>
    /// La geocerca como polígono: las esquinas, o el círculo con <paramref name="circleSides"/> lados
    /// (MobiControl solo guarda polígonos; Geomonitoreo usa 48).
    /// </summary>
    public IReadOnlyList<GeoPoint> Ring(int circleSides = 48)
    {
        if (Shape == GeofenceShape.Polygon) return Vertices;

        const double metersPerDegree = 111_320;
        var latRad = Center.Latitude * Math.PI / 180;
        var points = new List<GeoPoint>(circleSides);
        for (var i = 0; i < circleSides; i++)
        {
            var angle = 2 * Math.PI * i / circleSides;
            var lat = Center.Latitude + RadiusMeters * Math.Cos(angle) / metersPerDegree;
            var lng = Center.Longitude + RadiusMeters * Math.Sin(angle) / (metersPerDegree * Math.Cos(latRad));
            points.Add(GeoPoint.Create(Math.Round(lat, 7), Math.Round(lng, 7)).Value);
        }

        return points;
    }

    public bool Contains(GeoPoint point) => Shape switch
    {
        GeofenceShape.Circle => Center.DistanceTo(point) <= RadiusMeters,
        _ => InsidePolygon(point)
    };

    /// <summary>Evalúa una lectura. Con precisión peor que <paramref name="minAccuracyMeters"/> no se decide.</summary>
    public GeofenceStatus Evaluate(LocationReading? reading, int minAccuracyMeters)
    {
        if (reading is null) return GeofenceStatus.NoLocation;
        if (reading.AccuracyMeters > minAccuracyMeters) return GeofenceStatus.Unknown;
        return Contains(reading.Point) ? GeofenceStatus.Inside : GeofenceStatus.Outside;
    }

    /// <summary>Formato compacto "lat,lng;lat,lng" para guardar las esquinas en una sola columna.</summary>
    public string? EncodeVertices() =>
        Vertices.Count == 0 ? null : string.Join(';', Vertices.Select(v => v.ToString()));

    private static IReadOnlyList<GeoPoint> Decode(string? encoded)
    {
        if (string.IsNullOrWhiteSpace(encoded)) return [];

        return encoded
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split(','))
            .Select(parts => GeoPoint.Create(
                double.Parse(parts[0], CultureInfo.InvariantCulture),
                double.Parse(parts[1], CultureInfo.InvariantCulture)).Value)
            .ToArray();
    }

    /// <summary>Ray casting sobre el plano lat/lng: válido para polígonos de pocos kilómetros.</summary>
    private bool InsidePolygon(GeoPoint point)
    {
        var inside = false;
        for (int i = 0, j = Vertices.Count - 1; i < Vertices.Count; j = i++)
        {
            var a = Vertices[i];
            var b = Vertices[j];
            var crosses = (a.Latitude > point.Latitude) != (b.Latitude > point.Latitude) &&
                          point.Longitude < (b.Longitude - a.Longitude) * (point.Latitude - a.Latitude) /
                          (b.Latitude - a.Latitude) + a.Longitude;
            if (crosses) inside = !inside;
        }

        return inside;
    }
}
