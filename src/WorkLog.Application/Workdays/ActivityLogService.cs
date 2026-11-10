using WorkLog.Application.Abstractions;
using WorkLog.Application.Common;
using WorkLog.Domain.Access;
using WorkLog.Domain.Activities;
using WorkLog.Domain.Geo;
using WorkLog.Domain.Workdays;

namespace WorkLog.Application.Workdays;

/// <summary>Una jornada en el registro: quién, cuándo, cuánto y qué dejó (actividades y evidencias).</summary>
public sealed record ActivityLogRowDto(
    Guid WorkdayId,
    Guid MemberId,
    string MemberName,
    Guid? BranchId,
    string? BranchName,
    DateOnly Date,
    WorkdayStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    CloseKind? CloseKind,
    bool WasAdjusted,
    int LateMinutes,
    GeofenceStatus StartGeofence,
    GeofenceStatus EndGeofence,
    int ActivityCount,
    int WorkedMinutes,
    int ProductiveMinutes,
    int WithoutActivityMinutes,
    int PausedMinutes,
    int PhotoCount,
    int SignatureCount);

public sealed record ActivityLogTotalsDto(int Workdays, int People, int Activities, int WorkedMinutes, int ProductiveMinutes);

public sealed record ActivityLogDto(DateOnly From, DateOnly To, IReadOnlyList<ActivityLogRowDto> Rows, ActivityLogTotalsDto Totals);

public sealed record ActivityPauseDto(DateTimeOffset StartedAt, DateTimeOffset? EndedAt);

/// <summary>Una actividad con todo lo que registró el técnico al iniciarla y al cerrarla.</summary>
public sealed record ActivityDetailDto(
    Guid Id,
    string Name,
    string CategoryName,
    TimeClass TimeClass,
    string Color,
    string? WorkOrderNumber,
    string? WorkSiteName,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    bool Running,
    int Minutes,
    int PausedMinutes,
    IReadOnlyList<ActivityPauseDto> Pauses,
    ActivityOutcome? Outcome,
    string? OutcomeLabel,
    string? Observation,
    bool AutoFinished,
    GeofenceStatus StartGeofence,
    GeofenceStatus EndGeofence,
    int PhotoCount,
    bool HasSignature);

public sealed record EvidenceItemDto(Guid Id, Guid ActivityRecordId, EvidenceKind Kind, DateTimeOffset CapturedAt);

public sealed record WorkdayDetailDto(
    ActivityLogRowDto Workday,
    TimeOnly? ScheduledStart,
    TimeOnly? ScheduledEnd,
    string? StartSiteName,
    string? Note,
    IReadOnlyList<SegmentDto> Segments,
    IReadOnlyList<ActivityDetailDto> Activities,
    IReadOnlyList<EvidenceItemDto> Evidence);

/// <summary>Límites del registro de actividades.</summary>
public static class ActivityLog
{
    /// <summary>Tope del rango: un mes alcanza para revisar y exportar sin traer media base de datos.</summary>
    public const int MaxDays = 31;
}

/// <summary>Puerto de entrada: el registro de actividades de las personas, para el panel.</summary>
public interface IActivityLogService
{
    Task<OperationResult<ActivityLogDto>> ListAsync(DateOnly from, DateOnly to, Guid? memberId, Guid? branchId, CancellationToken ct = default);

    Task<OperationResult<WorkdayDetailDto>> GetWorkdayAsync(Guid workdayId, CancellationToken ct = default);

    Task<OperationResult<StoredFile>> OpenEvidenceAsync(Guid evidenceId, CancellationToken ct = default);

    /// <summary>Una fila por actividad (y una por jornada sin actividades), lista para Excel.</summary>
    Task<OperationResult<FileDownload>> ExportAsync(DateOnly from, DateOnly to, Guid? memberId, Guid? branchId, CancellationToken ct = default);
}

internal sealed class ActivityLogService(
    IMemberRepository members,
    IBranchRepository branches,
    IWorkdayRepository workdays,
    IEvidenceRepository evidence,
    IOutcomeSettingRepository outcomes,
    IFileStorage storage,
    ISpreadsheetWriter spreadsheet,
    TimeProvider clock) : IActivityLogService
{
    public async Task<OperationResult<ActivityLogDto>> ListAsync(DateOnly from, DateOnly to, Guid? memberId, Guid? branchId,
        CancellationToken ct = default)
    {
        var loaded = await LoadAsync(from, to, memberId, branchId, ct);
        if (!loaded.Succeeded) return OperationResult<ActivityLogDto>.From(loaded);

        var (list, people, branchNames) = loaded.Value!;
        var now = clock.GetUtcNow();
        var rows = list.Select(w => Row(w, people[w.MemberId], branchNames, now)).ToList();

        var totals = new ActivityLogTotalsDto(rows.Count, rows.Select(r => r.MemberId).Distinct().Count(),
            rows.Sum(r => r.ActivityCount), rows.Sum(r => r.WorkedMinutes), rows.Sum(r => r.ProductiveMinutes));
        return new ActivityLogDto(from, to, rows, totals);
    }

    public async Task<OperationResult<WorkdayDetailDto>> GetWorkdayAsync(Guid workdayId, CancellationToken ct = default)
    {
        var workday = await workdays.FindAsync(workdayId, ct);
        if (workday is null) return OperationResult<WorkdayDetailDto>.NotFound("La jornada no existe.");

        var member = await members.FindAsync(workday.MemberId, ct);
        if (member is null) return OperationResult<WorkdayDetailDto>.NotFound("La persona no existe.");

        var branchNames = (await branches.ListAsync(ct)).ToDictionary(b => b.Id, b => b.Name);
        var labels = (await outcomes.ListAsync(ct)).ToDictionary(o => o.Outcome, o => OutcomeSetting.LabelOf(o.Outcome));
        var now = clock.GetUtcNow();
        var (segments, _) = WorkdayTimeline.Build(workday, now);
        var minutesByActivity = segments.Where(s => s.ActivityId is not null)
            .GroupBy(s => s.ActivityId!.Value)
            .ToDictionary(g => g.Key, g => g.Sum(s => s.Minutes));

        var activities = workday.Activities.OrderBy(a => a.StartedAt).Select(a => new ActivityDetailDto(
            a.Id, a.Name, a.CategoryName, a.TimeClass, a.Color, a.WorkOrderNumber, a.WorkSiteName, a.StartedAt, a.EndedAt,
            a.IsOpen, minutesByActivity.GetValueOrDefault(a.Id), PausedMinutes(a, now),
            a.Pauses.OrderBy(p => p.StartedAt).Select(p => new ActivityPauseDto(p.StartedAt, p.EndedAt)).ToList(),
            a.Outcome, a.Outcome is { } o ? labels.GetValueOrDefault(o, OutcomeSetting.LabelOf(o)) : null, a.Observation,
            a.AutoFinished, a.StartGeofence, a.EndGeofence, a.PhotoCount, a.HasSignature)).ToList();

        var files = (await evidence.ListByWorkdayAsync(workday.Id, ct))
            .Select(e => new EvidenceItemDto(e.Id, e.ActivityRecordId, e.Kind, e.CapturedAt)).ToList();

        return new WorkdayDetailDto(Row(workday, member, branchNames, now), workday.ScheduledStart, workday.ScheduledEnd,
            workday.StartSiteName, workday.Note,
            segments.Select(s => new SegmentDto(s.Kind, s.Start, s.End, s.Minutes, s.Title, s.Color, s.TimeClass)).ToList(),
            activities, files);
    }

    public async Task<OperationResult<StoredFile>> OpenEvidenceAsync(Guid evidenceId, CancellationToken ct = default)
    {
        var item = await evidence.FindAsync(evidenceId, ct);
        if (item is null) return OperationResult<StoredFile>.NotFound("La evidencia no existe.");

        var file = await storage.OpenAsync(item.StorageKey, ct);
        return file is null ? OperationResult<StoredFile>.NotFound("El archivo ya no está disponible.") : file;
    }

    public async Task<OperationResult<FileDownload>> ExportAsync(DateOnly from, DateOnly to, Guid? memberId, Guid? branchId,
        CancellationToken ct = default)
    {
        var loaded = await LoadAsync(from, to, memberId, branchId, ct);
        if (!loaded.Succeeded) return OperationResult<FileDownload>.From(loaded);

        var (list, people, branchNames) = loaded.Value!;
        var zones = (await branches.ListAsync(ct)).ToDictionary(b => b.Id, b => LocalTime.Find(b.TimeZoneId));
        var labels = (await outcomes.ListAsync(ct)).ToDictionary(o => o.Outcome, o => OutcomeSetting.LabelOf(o.Outcome));
        var now = clock.GetUtcNow();

        string[] headers =
        [
            "Fecha", "Persona", "Correo", "Sede", "Inicio de jornada", "Fin de jornada", "Estado de la jornada",
            "Actividad", "Categoría", "Tipo de tiempo", "OT", "Sitio", "Inicio", "Fin", "Minutos", "Minutos en pausa",
            "Resultado", "Observación", "Geocerca al iniciar", "Fotos", "Firma"
        ];

        var rows = new List<IReadOnlyList<object?>>();
        foreach (var workday in list)
        {
            var person = people[workday.MemberId];
            var zone = workday.BranchId is { } b && zones.TryGetValue(b, out var z) ? z : LocalTime.Find(null);
            string Local(DateTimeOffset? at) => at is { } t ? TimeZoneInfo.ConvertTime(t, zone).ToString("dd/MM/yyyy HH:mm") : string.Empty;

            object?[] day =
            [
                workday.Date.ToString("dd/MM/yyyy"), person.FullName, person.Email,
                workday.BranchId is { } id ? branchNames.GetValueOrDefault(id) : null,
                Local(workday.StartedAt), Local(workday.EndedAt), StatusLabel(workday)
            ];

            var (segments, _) = WorkdayTimeline.Build(workday, now);
            var activities = workday.Activities.OrderBy(a => a.StartedAt).ToList();
            if (activities.Count == 0)
            {
                rows.Add([.. day, "(sin actividades)", null, null, null, null, null, null, null, null, null, null, null, null, null]);
                continue;
            }

            foreach (var a in activities)
            {
                rows.Add(
                [
                    .. day, a.Name, a.CategoryName, TimeClassLabel(a.TimeClass), a.WorkOrderNumber, a.WorkSiteName,
                    Local(a.StartedAt), Local(a.EndedAt), segments.Where(s => s.ActivityId == a.Id).Sum(s => s.Minutes),
                    PausedMinutes(a, now), a.Outcome is { } o ? labels.GetValueOrDefault(o, OutcomeSetting.LabelOf(o)) : null,
                    a.Observation, GeofenceLabel(a.StartGeofence), a.PhotoCount, a.HasSignature ? "Sí" : "No"
                ]);
            }
        }

        return new FileDownload($"registro-actividades-{from:yyyyMMdd}-{to:yyyyMMdd}.xlsx",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", spreadsheet.Write("Actividades", headers, rows));
    }

    private async Task<OperationResult<(IReadOnlyList<Workday> Workdays, IReadOnlyDictionary<Guid, Member> People, IReadOnlyDictionary<Guid, string> Branches)>>
        LoadAsync(DateOnly from, DateOnly to, Guid? memberId, Guid? branchId, CancellationToken ct)
    {
        if (to < from) return OperationResult<(IReadOnlyList<Workday>, IReadOnlyDictionary<Guid, Member>, IReadOnlyDictionary<Guid, string>)>
            .Invalid("La fecha final es anterior a la inicial.", "to");
        if (to.DayNumber - from.DayNumber + 1 > ActivityLog.MaxDays)
            return OperationResult<(IReadOnlyList<Workday>, IReadOnlyDictionary<Guid, Member>, IReadOnlyDictionary<Guid, string>)>
                .Invalid($"Consulta como máximo {ActivityLog.MaxDays} días a la vez.", "to");

        // Todas las personas, también las inactivas: el historial sigue siendo suyo.
        var everyone = await members.ListAllAsync(ct);
        var scope = everyone
            .Where(m => memberId is null || m.Id == memberId)
            .Where(m => branchId is null || m.BranchId == branchId)
            .ToDictionary(m => m.Id);

        var list = (await workdays.ListAsync(scope.Keys.ToList(), from, to, ct))
            .OrderByDescending(w => w.Date)
            .ThenBy(w => scope[w.MemberId].FullName)
            .ThenByDescending(w => w.StartedAt)
            .ToList();

        var branchNames = (await branches.ListAsync(ct)).ToDictionary(b => b.Id, b => b.Name);
        return (list, scope, branchNames);
    }

    private static ActivityLogRowDto Row(Workday w, Member person, IReadOnlyDictionary<Guid, string> branchNames, DateTimeOffset now)
    {
        var summary = WorkdayTimeline.Build(w, now).Summary;
        return new ActivityLogRowDto(w.Id, person.Id, person.FullName, w.BranchId,
            w.BranchId is { } b ? branchNames.GetValueOrDefault(b) : null, w.Date, w.Status, w.StartedAt, w.EndedAt, w.CloseKind,
            w.WasAdjusted, w.LateMinutes, w.StartGeofence, w.EndGeofence, summary.ActivityCount, summary.WorkedMinutes,
            summary.ProductiveMinutes, summary.WithoutActivityMinutes, summary.PausedMinutes,
            w.Activities.Sum(a => a.PhotoCount), w.Activities.Count(a => a.HasSignature));
    }

    private static int PausedMinutes(ActivityRecord activity, DateTimeOffset now) =>
        (int)activity.Pauses.Sum(p => ((p.EndedAt ?? now) - p.StartedAt).TotalMinutes);

    private static string StatusLabel(Workday w) => w.Status switch
    {
        WorkdayStatus.Open => "Abierta",
        WorkdayStatus.PendingReview => "Cierre automático por revisar",
        _ => w.CloseKind == CloseKind.Automatic ? "Cerrada automáticamente" : "Cerrada"
    };

    private static string TimeClassLabel(TimeClass timeClass) => timeClass switch
    {
        TimeClass.Productive => "Productivo",
        TimeClass.PaidNonProductive => "No productivo pagado",
        _ => "Improductivo"
    };

    private static string GeofenceLabel(GeofenceStatus status) => status switch
    {
        GeofenceStatus.Inside => "Dentro",
        GeofenceStatus.Outside => "Fuera",
        GeofenceStatus.Unknown => "Precisión insuficiente",
        GeofenceStatus.NoLocation => "Sin ubicación",
        _ => "No aplica"
    };
}
