using WorkLog.Domain.Activities;
using WorkLog.Domain.Common;
using WorkLog.Domain.Geo;

namespace WorkLog.Domain.Workdays;

public enum ActivityStatus
{
    Running = 1,
    Paused = 2,
    Finished = 3
}

/// <summary>
/// Datos de la actividad tal como eran al iniciarla. Se copian para que renombrar una actividad
/// o cambiar su categoría no reescriba la historia de días ya trabajados.
/// </summary>
public sealed record ActivitySnapshot(
    Guid ActivityTypeId,
    string Name,
    Guid CategoryId,
    string CategoryName,
    TimeClass TimeClass,
    string Color,
    Guid? WorkOrderId,
    string? WorkOrderNumber,
    Guid? WorkSiteId,
    string? WorkSiteName);

/// <summary>Lo que exige el cierre de una actividad según su tipo y el resultado elegido.</summary>
public sealed record FinishRequirements(bool Photo, bool Signature, bool Observation);

/// <summary>Datos con los que el usuario finaliza una actividad.</summary>
public sealed record ActivityFinish(
    DateTimeOffset At,
    ActivityOutcome Outcome,
    string? Observation,
    int PhotoCount,
    bool HasSignature,
    LocationReading? Location,
    GeofenceStatus Geofence);

/// <summary>Una actividad dentro de la jornada, con sus pausas.</summary>
public sealed class ActivityRecord : Entity
{
    private readonly List<ActivityPause> _pauses = [];

    private ActivityRecord()
    {
    }

    internal ActivityRecord(Guid? id, Guid workdayId, ActivitySnapshot snapshot, DateTimeOffset at, LocationReading? location, GeofenceStatus geofence)
    {
        // El teléfono puede proponer el id: así una pausa encolada sin conexión ya sabe a qué
        // actividad apunta antes de que el servidor haya visto el inicio.
        if (id is { } given && given != Guid.Empty) Id = given;
        WorkdayId = workdayId;
        ActivityTypeId = snapshot.ActivityTypeId;
        Name = snapshot.Name;
        CategoryId = snapshot.CategoryId;
        CategoryName = snapshot.CategoryName;
        TimeClass = snapshot.TimeClass;
        Color = snapshot.Color;
        WorkOrderId = snapshot.WorkOrderId;
        WorkOrderNumber = snapshot.WorkOrderNumber;
        WorkSiteId = snapshot.WorkSiteId;
        WorkSiteName = snapshot.WorkSiteName;
        StartedAt = at;
        Status = ActivityStatus.Running;
        StartLocation = LocationColumns.From(location);
        StartGeofence = geofence;
    }

    public Guid WorkdayId { get; private set; }
    public Guid ActivityTypeId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public Guid CategoryId { get; private set; }
    public string CategoryName { get; private set; } = string.Empty;
    public TimeClass TimeClass { get; private set; }
    public string Color { get; private set; } = string.Empty;
    public Guid? WorkOrderId { get; private set; }
    public string? WorkOrderNumber { get; private set; }
    public Guid? WorkSiteId { get; private set; }
    public string? WorkSiteName { get; private set; }
    public DateTimeOffset StartedAt { get; private set; }
    public DateTimeOffset? EndedAt { get; private set; }
    public ActivityStatus Status { get; private set; }
    public ActivityOutcome? Outcome { get; private set; }
    public string? Observation { get; private set; }

    /// <summary>Se cerró sola: al iniciar otra actividad o al cerrar la jornada.</summary>
    public bool AutoFinished { get; private set; }

    public LocationColumns StartLocation { get; private set; } = LocationColumns.Empty;
    public GeofenceStatus StartGeofence { get; private set; }
    public LocationColumns EndLocation { get; private set; } = LocationColumns.Empty;
    public GeofenceStatus EndGeofence { get; private set; }
    public int PhotoCount { get; private set; }
    public bool HasSignature { get; private set; }

    public IReadOnlyList<ActivityPause> Pauses => _pauses;

    public bool IsOpen => Status != ActivityStatus.Finished;

    public DateTimeOffset LastEventAt =>
        new[] { StartedAt, EndedAt ?? StartedAt }
            .Concat(_pauses.Select(p => p.EndedAt ?? p.StartedAt))
            .Max();

    /// <summary>Tiempo en pausa hasta <paramref name="until"/> (pausas abiertas cuentan hasta ese momento).</summary>
    public TimeSpan PausedTime(DateTimeOffset until) =>
        _pauses.Aggregate(TimeSpan.Zero, (total, p) => total + ((p.EndedAt ?? until) - p.StartedAt));

    /// <summary>Tiempo trabajado: de inicio a fin (o hasta ahora), sin pausas.</summary>
    public TimeSpan WorkedTime(DateTimeOffset now)
    {
        var end = EndedAt ?? now;
        var worked = end - StartedAt - PausedTime(end);
        return worked < TimeSpan.Zero ? TimeSpan.Zero : worked;
    }

    internal Result Pause(DateTimeOffset at)
    {
        if (Status != ActivityStatus.Running)
            return DomainError.Conflict("activity.not_running", "La actividad no está en curso.");

        _pauses.Add(new ActivityPause(Id, at));
        Status = ActivityStatus.Paused;
        return Result.Success();
    }

    internal Result Resume(DateTimeOffset at)
    {
        if (Status != ActivityStatus.Paused)
            return DomainError.Conflict("activity.not_paused", "La actividad no está en pausa.");

        _pauses[^1].End(at);
        Status = ActivityStatus.Running;
        return Result.Success();
    }

    internal Result Finish(ActivityFinish finish, FinishRequirements requirements)
    {
        if (Status == ActivityStatus.Finished)
            return DomainError.Conflict("activity.finished", "La actividad ya está finalizada.");

        if (!Enum.IsDefined(finish.Outcome))
            return DomainError.Validation("activity.outcome", "Elige el resultado.", "outcome");

        var observation = Text.CleanOrNull(finish.Observation);
        if (Guard.MaxLength(observation, "observation", 1000, "La observación") is { } lengthError) return lengthError;

        if (requirements.Observation && observation is null)
            return DomainError.Validation("activity.observation", "Escribe una observación para este resultado.", "observation");

        if (requirements.Photo && finish.PhotoCount < 1)
            return DomainError.Validation("activity.photo", "Esta actividad exige al menos una foto.", "photoCount");

        if (requirements.Signature && !finish.HasSignature)
            return DomainError.Validation("activity.signature", "Esta actividad exige la firma del cliente.", "hasSignature");

        Close(finish.At);
        Outcome = finish.Outcome;
        Observation = observation;
        PhotoCount = Math.Max(0, finish.PhotoCount);
        HasSignature = finish.HasSignature;
        EndLocation = LocationColumns.From(finish.Location);
        EndGeofence = finish.Geofence;
        return Result.Success();
    }

    /// <summary>Cierre automático: sin exigir evidencias, con resultado Completada.</summary>
    internal void AutoFinish(DateTimeOffset at, LocationReading? location, GeofenceStatus geofence)
    {
        Close(at);
        Outcome = ActivityOutcome.Completed;
        AutoFinished = true;
        EndLocation = LocationColumns.From(location);
        EndGeofence = geofence;
    }

    internal void AdjustStart(DateTimeOffset value) => StartedAt = value;

    internal void AdjustEnd(DateTimeOffset value) => EndedAt = value;

    private void Close(DateTimeOffset at)
    {
        if (Status == ActivityStatus.Paused) _pauses[^1].End(at);
        EndedAt = at;
        Status = ActivityStatus.Finished;
    }
}

/// <summary>Pausa de una actividad.</summary>
public sealed class ActivityPause : Entity
{
    private ActivityPause()
    {
    }

    internal ActivityPause(Guid activityRecordId, DateTimeOffset startedAt)
    {
        ActivityRecordId = activityRecordId;
        StartedAt = startedAt;
    }

    public Guid ActivityRecordId { get; private set; }
    public DateTimeOffset StartedAt { get; private set; }
    public DateTimeOffset? EndedAt { get; private set; }

    internal void End(DateTimeOffset at) => EndedAt = at;
}

/// <summary>Ubicación guardada junto a un evento (columnas opcionales).</summary>
public sealed record LocationColumns(double? Latitude, double? Longitude, double? AccuracyMeters)
{
    public static LocationColumns Empty { get; } = new(null, null, null);

    public bool HasValue => Latitude is not null && Longitude is not null;

    public static LocationColumns From(LocationReading? reading) =>
        reading is null ? Empty : new(reading.Point.Latitude, reading.Point.Longitude, reading.AccuracyMeters);
}
