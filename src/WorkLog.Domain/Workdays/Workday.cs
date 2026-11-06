using WorkLog.Domain.Common;
using WorkLog.Domain.Geo;
using WorkLog.Domain.Requests;
using WorkLog.Domain.Scheduling;

namespace WorkLog.Domain.Workdays;

public enum WorkdayStatus
{
    Open = 1,
    Closed = 2,

    /// <summary>Cerrada por el sistema y pendiente de que el supervisor confirme la hora de salida.</summary>
    PendingReview = 3
}

public enum CloseKind
{
    Manual = 1,
    Automatic = 2
}

/// <summary>Datos con los que se abre la jornada (ya validados por <see cref="WorkdayPolicy"/>).</summary>
public sealed record WorkdayOpening(
    Guid TenantId,
    Guid MemberId,
    Guid? BranchId,
    DateOnly Date,
    DateTimeOffset StartedAt,
    bool StartAdjusted,
    int LateMinutes,
    DayPlan Plan,
    LocationReading? Location,
    GeofenceStatus Geofence,
    string? SiteName);

/// <summary>
/// Jornada de una persona en un día. Es la raíz que protege el orden de los eventos: una sola
/// actividad abierta a la vez, nada antes del último evento registrado y nada después del cierre.
/// </summary>
public sealed class Workday : TenantEntity
{
    private readonly List<ActivityRecord> _activities = [];

    private Workday()
    {
    }

    private Workday(Guid tenantId) : base(tenantId)
    {
    }

    public Guid MemberId { get; private set; }
    public Guid? BranchId { get; private set; }
    public DateOnly Date { get; private set; }
    public DateTimeOffset StartedAt { get; private set; }

    /// <summary>El servidor corrigió la hora de inicio (llegó fuera de la ventana del turno).</summary>
    public bool StartAdjusted { get; private set; }

    public LocationColumns StartLocation { get; private set; } = LocationColumns.Empty;
    public GeofenceStatus StartGeofence { get; private set; }
    public string? StartSiteName { get; private set; }
    public TimeOnly? ScheduledStart { get; private set; }
    public TimeOnly? ScheduledEnd { get; private set; }
    public int LunchMinutes { get; private set; }
    public int LateMinutes { get; private set; }
    public DateTimeOffset? EndedAt { get; private set; }
    public LocationColumns EndLocation { get; private set; } = LocationColumns.Empty;
    public GeofenceStatus EndGeofence { get; private set; }
    public WorkdayStatus Status { get; private set; }
    public CloseKind? CloseKind { get; private set; }
    public string? Note { get; private set; }

    /// <summary>Alguna hora se corrigió por una solicitud de ajuste aprobada.</summary>
    public bool WasAdjusted { get; private set; }

    /// <summary>Sube con cada evento: dos eventos simultáneos de la misma jornada no pueden ganar los dos.</summary>
    public int Version { get; private set; }

    public IReadOnlyList<ActivityRecord> Activities => _activities;

    public bool IsOpen => Status == WorkdayStatus.Open;

    public ActivityRecord? CurrentActivity => _activities.LastOrDefault(a => a.IsOpen);

    /// <summary>Hora del último evento: ninguno nuevo puede quedar antes.</summary>
    public DateTimeOffset LastEventAt =>
        _activities.Select(a => a.LastEventAt).Append(StartedAt).Append(EndedAt ?? StartedAt).Max();

    public static Workday Start(WorkdayOpening opening, Guid? id = null) => new(opening.TenantId)
    {
        Id = id is { } given && given != Guid.Empty ? given : Guid.CreateVersion7(),
        MemberId = opening.MemberId,
        BranchId = opening.BranchId,
        Date = opening.Date,
        StartedAt = opening.StartedAt,
        StartAdjusted = opening.StartAdjusted,
        LateMinutes = opening.LateMinutes,
        ScheduledStart = opening.Plan.Start,
        ScheduledEnd = opening.Plan.End,
        LunchMinutes = opening.Plan.LunchMinutes,
        StartLocation = LocationColumns.From(opening.Location),
        StartGeofence = opening.Geofence,
        StartSiteName = opening.SiteName,
        Status = WorkdayStatus.Open
    };

    /// <summary>Inicia una actividad. Si había otra abierta, se finaliza sola a la misma hora.</summary>
    public Result<ActivityRecord> StartActivity(ActivitySnapshot snapshot, DateTimeOffset at, LocationReading? location, GeofenceStatus geofence, Guid? activityId = null)
    {
        if (EnsureAccepts(at) is { } error) return error;

        if (activityId is { } given && _activities.Any(a => a.Id == given))
            return DomainError.Conflict("activity.duplicate", "La actividad ya estaba registrada.");

        CurrentActivity?.AutoFinish(at, location, geofence);

        var record = new ActivityRecord(activityId, Id, snapshot, at, location, geofence);
        _activities.Add(record);
        Version++;
        return record;
    }

    public Result PauseActivity(Guid activityId, DateTimeOffset at)
    {
        if (EnsureAccepts(at) is { } error) return error;
        return Bump(Find(activityId) is { } activity ? activity.Pause(at) : NotFound());
    }

    public Result ResumeActivity(Guid activityId, DateTimeOffset at)
    {
        if (EnsureAccepts(at) is { } error) return error;
        return Bump(Find(activityId) is { } activity ? activity.Resume(at) : NotFound());
    }

    public Result FinishActivity(Guid activityId, ActivityFinish finish, FinishRequirements requirements)
    {
        if (EnsureAccepts(finish.At) is { } error) return error;
        return Bump(Find(activityId) is { } activity ? activity.Finish(finish, requirements) : NotFound());
    }

    /// <summary>Cierra la jornada. La actividad abierta se finaliza como Completada a la hora de cierre.</summary>
    public Result Close(DateTimeOffset at, LocationReading? location, GeofenceStatus geofence, string? note, CloseKind kind)
    {
        if (EnsureAccepts(at) is { } error) return error;

        var cleaned = Text.CleanOrNull(note);
        if (Guard.MaxLength(cleaned, "note", 1000, "La nota del día") is { } noteError) return noteError;

        CurrentActivity?.AutoFinish(at, location, geofence);

        EndedAt = at;
        EndLocation = LocationColumns.From(location);
        EndGeofence = geofence;
        Note = cleaned;
        CloseKind = kind;
        Status = kind == Workdays.CloseKind.Automatic ? WorkdayStatus.PendingReview : WorkdayStatus.Closed;
        Version++;
        return Result.Success();
    }

    /// <summary>El supervisor confirma un cierre automático.</summary>
    public Result ConfirmReview()
    {
        if (Status != WorkdayStatus.PendingReview)
            return DomainError.Conflict("workday.not_pending", "La jornada no está pendiente de revisión.");

        Status = WorkdayStatus.Closed;
        return Result.Success();
    }

    private DomainError? EnsureAccepts(DateTimeOffset at)
    {
        if (!IsOpen)
            return DomainError.Conflict("workday.closed", "La jornada ya está cerrada.");

        if (at < LastEventAt)
        {
            return DomainError.Conflict("workday.out_of_order",
                $"El evento es anterior al último registrado ({LastEventAt:HH\\:mm}).");
        }

        return null;
    }

    /// <summary>
    /// Tramos pagados: de inicio a fin (o hasta ahora) sin las pausas. El almuerzo se registra
    /// como pausa, así que no cuenta.
    /// </summary>
    public IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> WorkedIntervals(DateTimeOffset now)
    {
        var end = EndedAt ?? now;
        var result = new List<(DateTimeOffset, DateTimeOffset)>();
        var cursor = StartedAt;

        foreach (var pause in _activities.SelectMany(a => a.Pauses).OrderBy(p => p.StartedAt))
        {
            var pauseEnd = pause.EndedAt ?? end;
            if (pause.StartedAt > cursor) result.Add((cursor, pause.StartedAt < end ? pause.StartedAt : end));
            if (pauseEnd > cursor) cursor = pauseEnd;
        }

        if (end > cursor) result.Add((cursor, end));
        return result.Where(r => r.Item2 > r.Item1).ToList();
    }

    /// <summary>
    /// Corrige una hora por una solicitud aprobada. La corrección no puede desordenar la jornada:
    /// ninguna actividad queda antes del inicio, después del cierre ni encima de otra.
    /// </summary>
    public Result ApplyAdjustment(AdjustmentTarget target, Guid? activityId, DateTimeOffset value)
    {
        var ordered = _activities.OrderBy(a => a.StartedAt).ToList();

        switch (target)
        {
            case AdjustmentTarget.WorkdayStart:
                if (ordered.Count > 0 && value > ordered[0].StartedAt)
                    return Invalid("El inicio de la jornada no puede quedar después de la primera actividad.");
                if (EndedAt is { } end && value >= end)
                    return Invalid("El inicio debe ser antes del cierre.");
                StartedAt = value;
                break;

            case AdjustmentTarget.WorkdayEnd:
                if (EndedAt is null) return Invalid("La jornada sigue abierta: ciérrala antes de corregir su fin.");
                if (value <= StartedAt) return Invalid("El cierre debe ser después del inicio.");
                if (ordered.Any(a => (a.EndedAt ?? a.StartedAt) > value))
                    return Invalid("Hay actividades que terminan después de esa hora.");
                EndedAt = value;
                break;

            case AdjustmentTarget.ActivityStart:
            case AdjustmentTarget.ActivityEnd:
                var index = ordered.FindIndex(a => a.Id == activityId);
                if (index < 0) return DomainError.NotFound("activity.not_found", "La actividad no pertenece a esta jornada.");
                var activity = ordered[index];
                var previous = index > 0 ? ordered[index - 1] : null;
                var next = index < ordered.Count - 1 ? ordered[index + 1] : null;

                if (target == AdjustmentTarget.ActivityStart)
                {
                    if (value < StartedAt) return Invalid("La actividad no puede empezar antes de la jornada.");
                    if (previous?.EndedAt is { } prevEnd && value < prevEnd) return Invalid("Quedaría encima de la actividad anterior.");
                    var limit = activity.Pauses.Select(p => p.StartedAt).Append(activity.EndedAt ?? DateTimeOffset.MaxValue).Min();
                    if (value >= limit) return Invalid("El inicio debe ser antes del fin y de sus pausas.");
                    activity.AdjustStart(value);
                }
                else
                {
                    if (activity.EndedAt is null) return Invalid("La actividad sigue en curso.");
                    var floor = activity.Pauses.Select(p => p.EndedAt ?? p.StartedAt).Append(activity.StartedAt).Max();
                    if (value <= floor) return Invalid("El fin debe ser después del inicio y de sus pausas.");
                    if (next is not null && value > next.StartedAt) return Invalid("Quedaría encima de la actividad siguiente.");
                    if (EndedAt is { } closed && value > closed) return Invalid("El fin no puede quedar después del cierre de la jornada.");
                    activity.AdjustEnd(value);
                }

                break;
        }

        WasAdjusted = true;
        Version++;
        return Result.Success();

        static DomainError Invalid(string message) => DomainError.Validation("adjustment.invalid", message, "proposedValue");
    }

    private Result Bump(Result result)
    {
        if (result.IsSuccess) Version++;
        return result;
    }

    private ActivityRecord? Find(Guid activityId) => _activities.FirstOrDefault(a => a.Id == activityId);

    private static Result NotFound() =>
        DomainError.NotFound("activity.not_found", "La actividad no pertenece a esta jornada.");
}
