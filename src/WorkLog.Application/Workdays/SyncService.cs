using WorkLog.Application.Common;
using WorkLog.Domain.Activities;
using WorkLog.Domain.Sync;

namespace WorkLog.Application.Workdays;

/// <summary>
/// Evento de la cola del teléfono, en forma plana: cada tipo usa los campos que necesita. El
/// teléfono genera los ids de jornada y actividad para que los eventos encolados sin conexión
/// puedan referirse a registros que el servidor aún no ha visto.
/// </summary>
public sealed record SyncEvent(
    Guid ClientEventId,
    string Kind,
    DateTimeOffset OccurredAt,
    Guid? WorkdayId,
    Guid? ActivityId,
    Guid? ActivityTypeId,
    Guid? WorkOrderId,
    ActivityOutcome? Outcome,
    string? Observation,
    int? PhotoCount,
    bool? HasSignature,
    string? Note,
    LocationInput? Location);

public sealed record SyncEventResult(Guid ClientEventId, SyncOutcome Outcome, string? Message, string? Code, Guid? ResultId);

public sealed record SyncBatchResult(IReadOnlyList<SyncEventResult> Results, TodayDto? Today);

public static class SyncEventKinds
{
    public const string WorkdayStart = "workday.start";
    public const string WorkdayClose = "workday.close";
    public const string ActivityStart = "activity.start";
    public const string ActivityPause = "activity.pause";
    public const string ActivityResume = "activity.resume";
    public const string ActivityFinish = "activity.finish";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        WorkdayStart, WorkdayClose, ActivityStart, ActivityPause, ActivityResume, ActivityFinish
    };
}

public interface ISyncService
{
    /// <summary>Procesa la cola en orden. Un evento rechazado no detiene los siguientes.</summary>
    Task<OperationResult<SyncBatchResult>> ProcessAsync(IReadOnlyList<SyncEvent>? events, CancellationToken ct = default);
}

internal sealed class SyncService(IMyWorkdayService workdays) : ISyncService
{
    public const int MaxEventsPerBatch = 200;

    public async Task<OperationResult<SyncBatchResult>> ProcessAsync(IReadOnlyList<SyncEvent>? events, CancellationToken ct = default)
    {
        var batch = events ?? [];
        if (batch.Count > MaxEventsPerBatch)
            return OperationResult<SyncBatchResult>.Invalid($"Se reciben máximo {MaxEventsPerBatch} eventos por envío.", "events");

        var results = new List<SyncEventResult>(batch.Count);
        TodayDto? today = null;

        // Orden por hora real del evento; a igual hora, el orden en que el teléfono los encoló.
        foreach (var e in batch.Select((e, i) => (e, i)).OrderBy(x => x.e.OccurredAt).ThenBy(x => x.i).Select(x => x.e))
        {
            if (e.ClientEventId == Guid.Empty)
            {
                results.Add(new SyncEventResult(e.ClientEventId, SyncOutcome.Rejected, "Falta el id del evento.", "event.id", null));
                continue;
            }

            var dispatched = DispatchAsync(e, ct);
            if (dispatched is null)
            {
                results.Add(new SyncEventResult(e.ClientEventId, SyncOutcome.Rejected, $"Tipo de evento desconocido: {e.Kind}.", "event.kind", null));
                continue;
            }

            var result = await dispatched;

            if (result.Succeeded)
            {
                today = result.Value!.Today;
                var outcome = result.Value.Outcome;
                results.Add(new SyncEventResult(e.ClientEventId, outcome.Outcome, outcome.Message, null, outcome.ResultId));
            }
            else
            {
                results.Add(new SyncEventResult(e.ClientEventId, SyncOutcome.Rejected, result.Message, result.Code, null));
            }
        }

        if (today is null)
        {
            var current = await workdays.GetTodayAsync(ct);
            if (current.Succeeded) today = current.Value;
        }

        return new SyncBatchResult(results, today);
    }

    private Task<OperationResult<WorkdayCommandResult>>? DispatchAsync(SyncEvent e, CancellationToken ct) => e.Kind switch
    {
        SyncEventKinds.WorkdayStart => workdays.StartWorkdayAsync(new StartWorkdayCommand(e.ClientEventId, e.OccurredAt, e.Location, e.WorkdayId), ct),
        SyncEventKinds.WorkdayClose => workdays.CloseWorkdayAsync(new CloseWorkdayCommand(e.ClientEventId, e.OccurredAt, e.Location, e.Note), ct),
        SyncEventKinds.ActivityStart => workdays.StartActivityAsync(
            new StartActivityCommand(e.ClientEventId, e.OccurredAt, e.Location, e.ActivityId, e.ActivityTypeId, e.WorkOrderId), ct),
        SyncEventKinds.ActivityPause => workdays.PauseActivityAsync(e.ActivityId ?? Guid.Empty,
            new ActivityEventCommand(e.ClientEventId, e.OccurredAt, e.Location), ct),
        SyncEventKinds.ActivityResume => workdays.ResumeActivityAsync(e.ActivityId ?? Guid.Empty,
            new ActivityEventCommand(e.ClientEventId, e.OccurredAt, e.Location), ct),
        SyncEventKinds.ActivityFinish => workdays.FinishActivityAsync(e.ActivityId ?? Guid.Empty,
            new FinishActivityCommand(e.ClientEventId, e.OccurredAt, e.Location, e.Outcome ?? ActivityOutcome.Completed,
                e.Observation, e.PhotoCount ?? 0, e.HasSignature ?? false), ct),
        _ => null
    };
}
