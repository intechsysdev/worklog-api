using WorkLog.Application.Abstractions;
using WorkLog.Application.Activities;
using WorkLog.Application.Common;
using WorkLog.Application.Parameters;
using WorkLog.Application.Scheduling;
using WorkLog.Domain.Access;
using WorkLog.Domain.Activities;
using WorkLog.Domain.Common;
using WorkLog.Domain.Geo;
using WorkLog.Domain.Organization;
using WorkLog.Domain.Parameters;
using WorkLog.Domain.Scheduling;
using WorkLog.Domain.Sync;
using WorkLog.Domain.Workdays;

namespace WorkLog.Application.Workdays;

/// <summary>Puerto de entrada de la app móvil: la jornada propia.</summary>
public interface IMyWorkdayService
{
    Task<OperationResult<TodayDto>> GetTodayAsync(CancellationToken ct = default);
    Task<OperationResult<TimelineDto>> GetTimelineAsync(DateOnly? date, CancellationToken ct = default);
    Task<OperationResult<ActivityOptionsDto>> GetActivityOptionsAsync(CancellationToken ct = default);

    Task<OperationResult<WorkdayCommandResult>> StartWorkdayAsync(StartWorkdayCommand command, CancellationToken ct = default);
    Task<OperationResult<WorkdayCommandResult>> StartActivityAsync(StartActivityCommand command, CancellationToken ct = default);
    Task<OperationResult<WorkdayCommandResult>> PauseActivityAsync(Guid activityId, ActivityEventCommand command, CancellationToken ct = default);
    Task<OperationResult<WorkdayCommandResult>> ResumeActivityAsync(Guid activityId, ActivityEventCommand command, CancellationToken ct = default);
    Task<OperationResult<WorkdayCommandResult>> FinishActivityAsync(Guid activityId, FinishActivityCommand command, CancellationToken ct = default);
    Task<OperationResult<WorkdayCommandResult>> CloseWorkdayAsync(CloseWorkdayCommand command, CancellationToken ct = default);

    Task<OperationResult<int>> RecordLocationsAsync(LocationSamplesCommand command, CancellationToken ct = default);
    Task<OperationResult<EvidenceDto>> UploadEvidenceAsync(UploadEvidenceCommand command, CancellationToken ct = default);
}

internal sealed class MyWorkdayService(
    ICurrentActor actor,
    IMemberRepository members,
    IBranchRepository branches,
    IWorkSiteRepository sites,
    IWorkdayRepository workdays,
    IWorkOrderRepository workOrders,
    IActivityTypeRepository activityTypes,
    IActivityCategoryRepository categories,
    IOutcomeSettingRepository outcomes,
    ILocationPointRepository locations,
    IEvidenceRepository evidences,
    IProcessedEventRepository processedEvents,
    IPrivacyPolicyRepository policies,
    IAbsenceRepository absences,
    IHolidayRepository holidays,
    IScheduleTemplateRepository templates,
    IFileStorage storage,
    IScheduleCalculator calculator,
    IParameterProvider parameters,
    Devices.IDeviceOrchestrator devices,
    IWorkRequestRepository requests,
    IAuditTrail audit,
    IUnitOfWork unitOfWork,
    TimeProvider clock) : IMyWorkdayService
{
    /// <summary>Diferencia aceptada entre el reloj del teléfono y el del servidor.</summary>
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(2);

    /// <summary>Un evento más viejo que esto ya no se acepta desde la cola.</summary>
    private static readonly TimeSpan MaxQueueAge = TimeSpan.FromDays(7);

    private const int MaxSamplesPerBatch = 500;

    // ── Consultas ───────────────────────────────────────────────────────────

    public async Task<OperationResult<TodayDto>> GetTodayAsync(CancellationToken ct = default)
    {
        var context = await LoadAsync(ct);
        if (!context.Succeeded) return OperationResult<TodayDto>.From(context);
        return await BuildTodayAsync(context.Value!, ct);
    }

    public async Task<OperationResult<TimelineDto>> GetTimelineAsync(DateOnly? date, CancellationToken ct = default)
    {
        var loaded = await LoadAsync(ct);
        if (!loaded.Succeeded) return OperationResult<TimelineDto>.From(loaded);
        var context = loaded.Value!;

        var day = date ?? context.Today;
        var workday = context.Open is { } open && open.Date == day ? open : await workdays.FindByDateAsync(context.Member.Id, day, ct);
        var plan = await calculator.PlanAsync(context.Member, day, ct);

        if (workday is null)
            return new TimelineDto(day, plan.Start, plan.End, null, [], []);

        var now = clock.GetUtcNow();
        var (segments, _) = WorkdayTimeline.Build(workday, now);
        var outcomeLabels = (await outcomes.ListAsync(ct)).ToDictionary(o => o.Outcome, o => OutcomeSetting.LabelOf(o.Outcome));

        var entries = new List<TimelineEntryDto>
        {
            new("start", "Inicio de jornada", workday.StartedAt, null, 0, "#1E1F1D", false, null, workday.StartSiteName,
                null, null, false, 0, false, workday.StartGeofence)
        };

        foreach (var segment in segments)
        {
            if (segment.Kind == SegmentKind.Idle)
            {
                entries.Add(new TimelineEntryDto("idle", segment.Title, segment.Start, segment.End, segment.Minutes, segment.Color,
                    false, null, null, null, null, false, 0, false, GeofenceStatus.NotApplicable));
                continue;
            }

            var activity = workday.Activities.First(a => a.Id == segment.ActivityId);
            entries.Add(new TimelineEntryDto("activity", activity.Name, activity.StartedAt, activity.EndedAt, segment.Minutes,
                activity.Color, activity.IsOpen, activity.WorkOrderNumber, activity.WorkSiteName, activity.Outcome,
                activity.Outcome is { } o ? outcomeLabels.GetValueOrDefault(o) : null, activity.AutoFinished,
                activity.PhotoCount, activity.HasSignature, activity.StartGeofence));
        }

        if (workday.EndedAt is { } ended)
        {
            entries.Add(new TimelineEntryDto("close", workday.CloseKind == CloseKind.Automatic ? "Cierre automático" : "Cierre de jornada",
                ended, null, 0, "#1E1F1D", false, null, null, null, null, false, 0, false, workday.EndGeofence));
        }

        entries.Reverse();

        return new TimelineDto(day, plan.Start, plan.End, await ToDtoAsync(workday, now, ct),
            segments.Select(s => new SegmentDto(s.Kind, s.Start, s.End, s.Minutes, s.Title, s.Color, s.TimeClass)).ToList(),
            entries);
    }

    public async Task<OperationResult<ActivityOptionsDto>> GetActivityOptionsAsync(CancellationToken ct = default)
    {
        var loaded = await LoadAsync(ct);
        if (!loaded.Succeeded) return OperationResult<ActivityOptionsDto>.From(loaded);
        var context = loaded.Value!;

        var types = await activityTypes.ListAsync(false, ct);
        var typeMap = types.ToDictionary(t => t.Id);
        var categoryMap = (await categories.ListAsync(ct)).ToDictionary(c => c.Id);
        var siteNames = (await sites.ListAsync(true, ct)).ToDictionary(s => s.Id, s => s.Name);
        var orders = await workOrders.ListForAssigneeAsync(context.Member.Id, context.Today, ct);

        var assigned = orders
            .Where(o => o.IsWorkable && typeMap.ContainsKey(o.ActivityTypeId))
            .OrderBy(o => o.ScheduledTime ?? TimeOnly.MaxValue)
            .Select(o => new AssignedOptionDto(o.Id, o.Number, o.ActivityTypeId, typeMap[o.ActivityTypeId].Name,
                o.WorkSiteId is { } s ? siteNames.GetValueOrDefault(s) : null, o.ScheduledTime, o.Status))
            .ToList();

        var catalog = types
            .Where(t => t.IsActive && categoryMap.TryGetValue(t.CategoryId, out var c) && c.IsActive)
            .OrderBy(t => categoryMap[t.CategoryId].TimeClass)
            .ThenBy(t => t.Name)
            .Select(t => new CatalogOptionDto(t.Id, t.Name, categoryMap[t.CategoryId].Name, categoryMap[t.CategoryId].TimeClass,
                categoryMap[t.CategoryId].Color, t.RequiresPhoto, t.RequiresSignature, t.Observation))
            .ToList();

        return new ActivityOptionsDto(assigned, catalog, (await outcomes.ListAsync(ct)).OrderBy(o => o.Outcome).Select(CatalogService.ToDto).ToList());
    }

    // ── Jornada ─────────────────────────────────────────────────────────────

    public Task<OperationResult<WorkdayCommandResult>> StartWorkdayAsync(StartWorkdayCommand command, CancellationToken ct = default) =>
        ExecuteAsync("workday.start", command.ClientEventId, command.OccurredAt, async (context, at, queued) =>
        {
            if (context.Member.Usage != MemberUsage.Mobile)
                return DomainError.Forbidden("workday.web_only", "Tu usuario es solo de panel web: no registra jornada.");

            if (await policies.CurrentAsync(context.Today, ct) is { } policy && !context.Member.HasAccepted(policy.Version))
                return DomainError.Forbidden("workday.policy", "Acepta la política de tratamiento de datos antes de iniciar la jornada.");

            if (context.Open is { } open)
            {
                if (command.WorkdayId is { } requested && requested == open.Id)
                    return new Applied(SyncOutcome.Accepted, null, open.Id);

                if (open.Date >= LocalTime.DateOf(at, context.TimeZone))
                    return DomainError.Conflict("workday.already_open", "Ya tienes una jornada abierta.");

                CloseForgotten(open, context);
            }

            var date = LocalTime.DateOf(at, context.TimeZone);
            if (await workdays.FindByDateAsync(context.Member.Id, date, ct) is { IsOpen: false })
                return DomainError.Conflict("workday.already_closed", "Ya cerraste la jornada de hoy. Si fue un error, solicita un ajuste.");

            var plan = await calculator.PlanAsync(context.Member, date, ct);
            var decision = WorkdayPolicy.EvaluateStart(plan, at, context.TimeZone,
                context.Parameters[ParameterKey.EarlyStartMinutes], context.Parameters[ParameterKey.LateToleranceMinutes], queued);
            if (decision.IsFailure) return decision.Error!;

            var reading = ToReading(command.Location, at);
            if (reading.IsFailure) return reading.Error!;

            var mainSite = context.Branch?.MainSiteId is { } siteId ? await sites.FindAsync(siteId, ct) : null;
            var geofence = Evaluate(mainSite, reading.Value, context);

            // Con "apps corporativas solo en jornada", MobiControl habilita las apps antes de abrir.
            if (!await devices.OnWorkdayOpeningAsync(context.Member, ct))
                return DomainError.Forbidden("device.unreachable",
                    "No se pudieron habilitar tus apps de trabajo: MobiControl no responde. Intenta de nuevo o avisa a soporte.");

            var workday = Workday.Start(new WorkdayOpening(actor.TenantId, context.Member.Id, context.Member.BranchId, date,
                decision.Value.StartAt, decision.Value.Adjusted, decision.Value.LateMinutes, plan, reading.Value, geofence,
                mainSite?.Name ?? context.Branch?.Name), command.WorkdayId);

            workdays.Add(workday);
            context.Open = workday;
            RecordPoint(workday, null, LocationEvent.WorkdayStart, reading.Value, geofence);

            var local = TimeZoneInfo.ConvertTime(decision.Value.StartAt, context.TimeZone);
            var message = decision.Value.Adjusted
                ? $"Fuera de la ventana del turno (desde {local:HH\\:mm}). Se registró a las {local:HH\\:mm}."
                : null;

            audit.Record("workday.started",
                $"Inició jornada{(queued ? " sin conexión" : "")}{(decision.Value.LateMinutes > 0 ? $" con {decision.Value.LateMinutes} min de retraso" : "")}" +
                $"{(decision.Value.Adjusted ? " (hora ajustada al inicio de la ventana)" : "")}.",
                "Registros", workday.Id.ToString(), after: new { StartedAt = local, Geofence = geofence.ToString() }, occurredAt: queued ? at : null);

            return new Applied(decision.Value.Adjusted ? SyncOutcome.Adjusted : SyncOutcome.Accepted, message, workday.Id);
        }, ct);

    public Task<OperationResult<WorkdayCommandResult>> CloseWorkdayAsync(CloseWorkdayCommand command, CancellationToken ct = default) =>
        ExecuteAsync("workday.close", command.ClientEventId, command.OccurredAt, async (context, at, queued) =>
        {
            if (context.Open is not { } workday)
                return DomainError.Conflict("workday.not_open", "No tienes una jornada abierta.");

            var reading = ToReading(command.Location, at);
            if (reading.IsFailure) return reading.Error!;

            var mainSite = context.Branch?.MainSiteId is { } siteId ? await sites.FindAsync(siteId, ct) : null;
            var geofence = Evaluate(mainSite, reading.Value, context);
            var current = workday.CurrentActivity;

            var closed = workday.Close(at, reading.Value, geofence, command.Note, CloseKind.Manual);
            if (closed.IsFailure) return closed.Error!;

            await ApplyAutoFinishedAsync(current, ct);
            RecordPoint(workday, null, LocationEvent.WorkdayClose, reading.Value, geofence);
            await devices.OnWorkdayClosedAsync(context.Member, ct);

            var summary = WorkdayTimeline.Build(workday, at).Summary;
            audit.Record("workday.closed",
                $"Cerró la jornada{(queued ? " sin conexión" : "")}: {ScheduleTemplate.FormatHours(summary.WorkedMinutes)} registradas.",
                "Registros", workday.Id.ToString(), after: new { EndedAt = TimeZoneInfo.ConvertTime(at, context.TimeZone), summary.WorkedMinutes },
                occurredAt: queued ? at : null);

            return new Applied(SyncOutcome.Accepted, null, workday.Id);
        }, ct);

    // ── Actividades ─────────────────────────────────────────────────────────

    public Task<OperationResult<WorkdayCommandResult>> StartActivityAsync(StartActivityCommand command, CancellationToken ct = default) =>
        ExecuteAsync("activity.start", command.ClientEventId, command.OccurredAt, async (context, at, queued) =>
        {
            if (context.Open is not { } workday)
                return DomainError.Conflict("workday.not_open", "Inicia la jornada antes de registrar actividades.");

            WorkOrder? order = null;
            Guid? typeId = command.ActivityTypeId;

            if (command.WorkOrderId is { } orderId)
            {
                order = await workOrders.FindAsync(orderId, ct);
                if (order is null) return DomainError.NotFound("work_order.not_found", "La OT no existe.");
                if (order.AssigneeId != context.Member.Id)
                    return DomainError.Forbidden("work_order.not_assigned", "La OT no está asignada a ti.");
                if (!order.IsWorkable)
                    return DomainError.Conflict("work_order.closed", $"La {order.Number} ya está cerrada.");
                typeId = order.ActivityTypeId;
            }

            if (typeId is not { } activityTypeId || await activityTypes.FindAsync(activityTypeId, ct) is not { } type)
                return DomainError.Validation("activity.type", "Elige la actividad.", "activityTypeId");

            if (!type.IsActive && order is null)
                return DomainError.Validation("activity.inactive", "La actividad ya no está en el catálogo.", "activityTypeId");

            var category = await categories.FindAsync(type.CategoryId, ct);
            var site = order?.WorkSiteId is { } siteId ? await sites.FindAsync(siteId, ct) : null;

            var reading = ToReading(command.Location, at);
            if (reading.IsFailure) return reading.Error!;
            var geofence = Evaluate(site, reading.Value, context);

            var snapshot = new ActivitySnapshot(type.Id, type.Name, type.CategoryId, category?.Name ?? string.Empty,
                category?.TimeClass ?? TimeClass.Productive, category?.Color ?? "#AF1839",
                order?.Id, order?.Number, site?.Id, site?.Name);

            var previous = workday.CurrentActivity;
            var started = workday.StartActivity(snapshot, at, reading.Value, geofence, command.ActivityId);
            if (started.IsFailure) return started.Error!;

            await ApplyAutoFinishedAsync(previous, ct);
            order?.MarkStarted();
            RecordPoint(workday, started.Value.Id, LocationEvent.ActivityStart, reading.Value, geofence);

            if (queued)
            {
                audit.Record("activity.started_offline", $"Inició «{type.Name}»{(order is null ? "" : $" ({order.Number})")} sin conexión.",
                    "Registros", started.Value.Id.ToString(), occurredAt: at);
            }

            return new Applied(SyncOutcome.Accepted, null, started.Value.Id);
        }, ct);

    public Task<OperationResult<WorkdayCommandResult>> PauseActivityAsync(Guid activityId, ActivityEventCommand command, CancellationToken ct = default) =>
        ExecuteAsync("activity.pause", command.ClientEventId, command.OccurredAt, (context, at, _) =>
            Task.FromResult(ApplyToActivity(context, activityId, at, command.Location, LocationEvent.Pause,
                workday => workday.PauseActivity(activityId, at))), ct);

    public Task<OperationResult<WorkdayCommandResult>> ResumeActivityAsync(Guid activityId, ActivityEventCommand command, CancellationToken ct = default) =>
        ExecuteAsync("activity.resume", command.ClientEventId, command.OccurredAt, (context, at, _) =>
            Task.FromResult(ApplyToActivity(context, activityId, at, command.Location, LocationEvent.Resume,
                workday => workday.ResumeActivity(activityId, at))), ct);

    public Task<OperationResult<WorkdayCommandResult>> FinishActivityAsync(Guid activityId, FinishActivityCommand command, CancellationToken ct = default) =>
        ExecuteAsync("activity.finish", command.ClientEventId, command.OccurredAt, async (context, at, _) =>
        {
            if (context.Open is not { } workday)
                return DomainError.Conflict("workday.not_open", "No tienes una jornada abierta.");

            if (workday.Activities.FirstOrDefault(a => a.Id == activityId) is not { } activity)
                return DomainError.NotFound("activity.not_found", "La actividad no pertenece a tu jornada abierta.");

            var type = await activityTypes.FindAsync(activity.ActivityTypeId, ct);
            var outcomeSetting = (await outcomes.ListAsync(ct)).FirstOrDefault(o => o.Outcome == command.Outcome);
            var requirements = new FinishRequirements(
                type?.RequiresPhoto ?? false,
                type?.RequiresSignature ?? false,
                type?.Observation == ObservationPolicy.Always || (outcomeSetting?.RequiresObservation ?? false));

            var reading = ToReading(command.Location, at);
            if (reading.IsFailure) return reading.Error!;

            var site = activity.WorkSiteId is { } siteId ? await sites.FindAsync(siteId, ct) : null;
            var geofence = Evaluate(site, reading.Value, context);

            var finished = workday.FinishActivity(activityId, new ActivityFinish(at, command.Outcome, command.Observation,
                command.PhotoCount, command.HasSignature, reading.Value, geofence), requirements);
            if (finished.IsFailure) return finished.Error!;

            if (activity.WorkOrderId is { } orderId && await workOrders.FindAsync(orderId, ct) is { } order)
                order.ApplyOutcome(command.Outcome);

            RecordPoint(workday, activity.Id, LocationEvent.ActivityFinish, reading.Value, geofence);
            return new Applied(SyncOutcome.Accepted, null, activity.Id);
        }, ct);

    // ── Ubicación y evidencias ──────────────────────────────────────────────

    public async Task<OperationResult<int>> RecordLocationsAsync(LocationSamplesCommand command, CancellationToken ct = default)
    {
        var samples = command.Samples ?? [];
        if (samples.Count == 0) return 0;
        if (samples.Count > MaxSamplesPerBatch)
            return OperationResult<int>.Invalid($"Se reciben máximo {MaxSamplesPerBatch} puntos por envío.", "samples");

        var loaded = await LoadAsync(ct);
        if (!loaded.Succeeded) return OperationResult<int>.From(loaded);
        var context = loaded.Value!;

        // Fuera de la jornada no se guarda ubicación: es lo que promete la política de datos.
        if (context.Open is not { } workday) return 0;

        var now = clock.GetUtcNow();
        var siteCache = new Dictionary<Guid, WorkSite?>();
        var mainSite = context.Branch?.MainSiteId is { } mainId ? await sites.FindAsync(mainId, ct) : null;
        var stored = 0;

        foreach (var sample in samples)
        {
            var at = sample.CapturedAt ?? now;
            if (at < workday.StartedAt || at > now + ClockSkew) continue;

            var reading = ToReading(sample, at);
            if (reading.IsFailure || reading.Value is null) continue;

            var activity = workday.Activities.LastOrDefault(a => a.StartedAt <= at && (a.EndedAt is null || a.EndedAt >= at));
            WorkSite? site = mainSite;
            if (activity?.WorkSiteId is { } siteId)
            {
                if (!siteCache.TryGetValue(siteId, out site)) siteCache[siteId] = site = await sites.FindAsync(siteId, ct);
            }

            RecordPoint(workday, activity?.Id, LocationEvent.Sample, reading.Value, Evaluate(site, reading.Value, context));
            stored++;
        }

        await unitOfWork.SaveChangesAsync(ct);
        return stored;
    }

    public async Task<OperationResult<EvidenceDto>> UploadEvidenceAsync(UploadEvidenceCommand command, CancellationToken ct = default)
    {
        if (command.ClientId == Guid.Empty)
            return OperationResult<EvidenceDto>.Invalid("Falta el identificador de la evidencia.", "clientId");

        if (await evidences.FindByClientIdAsync(command.ClientId, ct) is { } existing)
            return ToDto(existing);

        var workday = await workdays.FindOpenAsync(actor.MemberId, ct);
        var activity = workday?.Activities.FirstOrDefault(a => a.Id == command.ActivityId);

        // La foto puede llegar después de cerrar (esperaba wifi): se busca también en la jornada del día.
        if (activity is null)
        {
            var member = await members.FindAsync(actor.MemberId, ct);
            var zone = await ZoneAsync(member, ct);
            var at = command.CapturedAt ?? clock.GetUtcNow();
            workday = await workdays.FindByDateAsync(actor.MemberId, LocalTime.DateOf(at, zone), ct);
            activity = workday?.Activities.FirstOrDefault(a => a.Id == command.ActivityId);
        }

        if (workday is null || activity is null)
            return OperationResult<EvidenceDto>.NotFound("La actividad no pertenece a tus jornadas.");

        var id = Guid.CreateVersion7();
        var extension = command.ContentType switch
        {
            "image/png" => "png",
            "image/webp" => "webp",
            _ => "jpg"
        };
        var key = $"{actor.TenantId:N}/evidences/{workday.Id:N}/{id:N}.{extension}";

        var created = Evidence.Create(actor.TenantId, workday.Id, activity.Id, actor.MemberId, command.Kind, key,
            command.ContentType, command.Length, command.CapturedAt ?? clock.GetUtcNow(), command.ClientId);
        if (created.IsFailure) return OperationResult<EvidenceDto>.From(created.Error!);

        await storage.SaveAsync(key, command.Content, command.ContentType, ct);
        evidences.Add(created.Value);

        try
        {
            await unitOfWork.SaveChangesAsync(ct);
        }
        catch (PersistenceConflictException)
        {
            // Dos subidas simultáneas del mismo archivo: gana la primera.
            return await evidences.FindByClientIdAsync(command.ClientId, ct) is { } winner
                ? ToDto(winner)
                : OperationResult<EvidenceDto>.Conflict("Reintenta la subida.");
        }

        return ToDto(created.Value);
    }

    // ── Motor de comandos ───────────────────────────────────────────────────

    private sealed record Applied(SyncOutcome Outcome, string? Message, Guid? ResultId);

    private sealed class Context(Member member, Branch? branch, TimeZoneInfo zone, DateOnly today, EffectiveParameters parameters, Workday? open)
    {
        public Member Member { get; } = member;
        public Branch? Branch { get; } = branch;
        public TimeZoneInfo TimeZone { get; } = zone;
        public DateOnly Today { get; } = today;
        public EffectiveParameters Parameters { get; } = parameters;
        public Workday? Open { get; set; } = open;
    }

    /// <summary>
    /// Ejecuta un evento de la jornada de forma idempotente: si el teléfono ya lo envió, devuelve
    /// la misma respuesta sin repetir el efecto. La hora del evento es la del teléfono (puede venir
    /// de la cola sin conexión), acotada para no aceptar horas futuras ni demasiado viejas.
    /// </summary>
    private async Task<OperationResult<WorkdayCommandResult>> ExecuteAsync(
        string kind, Guid? clientEventId, DateTimeOffset? occurredAt,
        Func<Context, DateTimeOffset, bool, Task<Result<Applied>>> action, CancellationToken ct)
    {
        var loaded = await LoadAsync(ct);
        if (!loaded.Succeeded) return OperationResult<WorkdayCommandResult>.From(loaded);
        var context = loaded.Value!;

        if (clientEventId is { } eventId && await processedEvents.FindAsync(context.Member.Id, eventId, ct) is { } processed)
            return await ReplayAsync(processed, context, ct);

        var now = clock.GetUtcNow();
        var at = occurredAt ?? now;

        if (at > now + ClockSkew)
            return OperationResult<WorkdayCommandResult>.Invalid("La hora del teléfono está adelantada. Revisa la fecha y hora automáticas.", "occurredAt", "event.future");

        if (at < now - MaxQueueAge)
            return OperationResult<WorkdayCommandResult>.Invalid("El evento tiene más de 7 días: solicita un ajuste a tu supervisor.", "occurredAt", "event.too_old");

        if (at > now) at = now;
        var queued = now - at > ClockSkew;

        var applied = await action(context, at, queued);

        if (clientEventId is { } id)
        {
            processedEvents.Add(ProcessedClientEvent.Create(actor.TenantId, context.Member.Id, id, kind, at, now,
                applied.IsSuccess ? applied.Value.Outcome : SyncOutcome.Rejected,
                applied.IsSuccess ? applied.Value.Message : applied.Error!.Message,
                applied.IsSuccess ? applied.Value.ResultId : null));
        }

        // Un rechazo también se guarda (si traía id): el reintento debe recibir el mismo rechazo.
        if (applied.IsFailure && clientEventId is null)
            return OperationResult<WorkdayCommandResult>.From(applied.Error!);

        try
        {
            await unitOfWork.SaveChangesAsync(ct);
        }
        catch (PersistenceConflictException)
        {
            return OperationResult<WorkdayCommandResult>.Conflict("Llegó otro registro al mismo tiempo. Reintenta.", "event.concurrent");
        }

        if (applied.IsFailure) return OperationResult<WorkdayCommandResult>.From(applied.Error!);

        var today = await BuildTodayAsync(context, ct);
        return new WorkdayCommandResult(new CommandOutcome(applied.Value.Outcome, applied.Value.Message, applied.Value.ResultId), today);
    }

    private async Task<OperationResult<WorkdayCommandResult>> ReplayAsync(ProcessedClientEvent processed, Context context, CancellationToken ct)
    {
        if (processed.Outcome == SyncOutcome.Rejected)
            return OperationResult<WorkdayCommandResult>.Conflict(processed.Message ?? "El servidor rechazó este registro.", "event.rejected");

        var today = await BuildTodayAsync(context, ct);
        return new WorkdayCommandResult(new CommandOutcome(processed.Outcome, processed.Message, processed.ResultId), today);
    }

    private Result<Applied> ApplyToActivity(Context context, Guid activityId, DateTimeOffset at, LocationInput? location,
        LocationEvent locationEvent, Func<Workday, Result> apply)
    {
        if (context.Open is not { } workday)
            return DomainError.Conflict("workday.not_open", "No tienes una jornada abierta.");

        var reading = ToReading(location, at);
        if (reading.IsFailure) return reading.Error!;

        var result = apply(workday);
        if (result.IsFailure) return result.Error!;

        RecordPoint(workday, activityId, locationEvent, reading.Value, reading.Value is null ? GeofenceStatus.NoLocation : GeofenceStatus.NotApplicable);
        return new Applied(SyncOutcome.Accepted, null, activityId);
    }

    /// <summary>
    /// Jornada de un día anterior que quedó abierta (el teléfono no cerró y el proceso automático
    /// aún no pasó): se cierra al fin de su turno para no mezclar dos días.
    /// </summary>
    private void CloseForgotten(Workday open, Context context)
    {
        var end = open.ScheduledEnd is { } scheduled
            ? ShiftWindow.ToInstant(open.Date, scheduled, context.TimeZone)
            : open.LastEventAt;
        if (end < open.LastEventAt) end = open.LastEventAt;

        if (open.Close(end, null, GeofenceStatus.NoLocation, null, CloseKind.Automatic).IsSuccess)
        {
            audit.RecordSystem(actor.TenantId, "workday.auto_closed",
                $"Cerró la jornada del {open.Date:dd/MM} de {context.Member.FullName} al fin del turno (quedó abierta).",
                "Registros", open.Id.ToString());
        }
    }

    private async Task ApplyAutoFinishedAsync(ActivityRecord? previous, CancellationToken ct)
    {
        if (previous is { AutoFinished: true, WorkOrderId: { } orderId } && await workOrders.FindAsync(orderId, ct) is { } order)
            order.ApplyOutcome(ActivityOutcome.Completed);
    }

    private void RecordPoint(Workday workday, Guid? activityId, LocationEvent locationEvent, LocationReading? reading, GeofenceStatus geofence)
    {
        if (reading is null) return;
        locations.Add(LocationPoint.Record(actor.TenantId, workday.MemberId, workday.Id, activityId, locationEvent, reading, geofence, clock.GetUtcNow()));
    }

    private static GeofenceStatus Evaluate(WorkSite? site, LocationReading? reading, Context context)
    {
        if (reading is null) return GeofenceStatus.NoLocation;
        if (site is null) return GeofenceStatus.NotApplicable;
        return site.Geofence.Evaluate(reading, context.Parameters[ParameterKey.MinGpsAccuracyMeters]);
    }

    private static Result<LocationReading?> ToReading(LocationInput? input, DateTimeOffset at)
    {
        if (input is null) return Result<LocationReading?>.Success(null);
        var reading = LocationReading.Create(input.Latitude, input.Longitude, input.Accuracy, input.CapturedAt ?? at);
        return reading.IsSuccess ? Result<LocationReading?>.Success(reading.Value) : reading.Error!;
    }

    private async Task<OperationResult<Context>> LoadAsync(CancellationToken ct)
    {
        var member = await members.FindAsync(actor.MemberId, ct);
        if (member is null) return OperationResult<Context>.NotFound("Tu usuario no existe en WorkLog.");

        var branch = member.BranchId is { } branchId ? await branches.FindAsync(branchId, ct) : null;
        var zone = LocalTime.Find(branch?.TimeZoneId);
        var today = LocalTime.Today(clock, zone);
        var parameterSet = await parameters.ForBranchAsync(branch, today, ct);
        var open = await workdays.FindOpenAsync(member.Id, ct);

        return new Context(member, branch, zone, today, parameterSet, open);
    }

    private async Task<TimeZoneInfo> ZoneAsync(Member? member, CancellationToken ct)
    {
        var branch = member?.BranchId is { } id ? await branches.FindAsync(id, ct) : null;
        return LocalTime.Find(branch?.TimeZoneId);
    }

    private async Task<TodayDto> BuildTodayAsync(Context context, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var today = LocalTime.Today(clock, context.TimeZone);
        var plan = await calculator.PlanAsync(context.Member, today, ct);
        var p = context.Parameters;

        var workday = context.Open ?? await workdays.FindByDateAsync(context.Member.Id, today, ct);

        StartWindowDto? window = null;
        if (ShiftWindow.For(plan, context.TimeZone, p[ParameterKey.EarlyStartMinutes]) is { } shift)
        {
            var check = WorkdayPolicy.EvaluateStart(plan, now, context.TimeZone, p[ParameterKey.EarlyStartMinutes], p[ParameterKey.LateToleranceMinutes], false);
            window = new StartWindowDto(shift.OpensAt, shift.ShiftStart, shift.ShiftEnd, check.IsSuccess && workday is null, check.Error?.Message);
        }

        AbsenceInfoDto? absence = null;
        if (plan.Kind == DayKind.Absence)
        {
            var approved = await absences.ListApprovedAsync([context.Member.Id], today, today, ct);
            var covering = approved.Count > 0 ? approved[0] : null;
            if (covering is not null)
            {
                var approver = covering.ResolvedBy is { } by ? (await members.FindAsync(by, ct))?.FullName : null;
                absence = new AbsenceInfoDto(ShiftResolver.AbsenceLabel(covering.Type), covering.From, covering.To, approver);
            }
        }

        NextShiftDto? next = null;
        if (!plan.IsWorkingDay && await calculator.NextWorkingDayAsync(context.Member, today, ct) is { } upcoming)
        {
            var country = context.Branch?.CountryCode ?? Company.DefaultCountry;
            var between = await holidays.ListAsync(country, today.AddDays(1), upcoming.Date.AddDays(-1), ct);
            next = new NextShiftDto(upcoming.Date, upcoming.Start!.Value, upcoming.End!.Value, upcoming.TemplateName,
                between.Select(h => $"El {h.Date:dd/MM} es festivo ({h.Name}).").ToList());
        }

        var policy = await policies.CurrentAsync(today, ct);
        var dayLetters = plan.TemplateId is { } templateId ? (await templates.FindAsync(templateId, ct))?.Days.ToLetters() : null;

        var deviceSettings = await devices.SettingsAsync(ct);
        var controlled = await devices.ControlsAsync(context.Member, ct);
        var shared = controlled && (await devices.DevicesAsync(ct)).GetValueOrDefault(context.Member.DeviceId!) is { IsShared: true };

        var overtimeUntil = (await requests.ListAsync([context.Member.Id], today, today, Domain.Requests.RequestStatus.Approved, ct))
            .Where(r => r.Type == Domain.Requests.RequestType.Overtime)
            .Select(r => r.OvertimeTo)
            .Max();

        DateTimeOffset? autoCloseAt = null;
        if (plan.End is { } planEnd)
        {
            var end = ShiftWindow.ToInstant(today, overtimeUntil is { } o && o > planEnd ? o : planEnd, context.TimeZone);
            autoCloseAt = end.AddMinutes(p[ParameterKey.AutoCloseGraceMinutes]);
        }

        var device = new DeviceStatusDto(controlled, shared, context.Member.DeviceModel,
            controlled ? deviceSettings.WarningMinutes : [], deviceSettings.LogoutSharedOnClose,
            controlled ? deviceSettings.CorporateApps : [], deviceSettings.AlwaysAvailableApps);

        return new TodayDto(
            now,
            today,
            context.TimeZone.Id,
            context.Member.FullName,
            context.Branch?.Name,
            context.Member.SupervisorId is { } supervisorId ? (await members.FindAsync(supervisorId, ct))?.FullName : null,
            new DayPlanDto(plan.Kind, plan.Start, plan.End, plan.LunchMinutes, plan.ScheduledMinutes, plan.Label, plan.TemplateName, dayLetters),
            window,
            workday is null ? null : await ToDtoAsync(workday, now, ct),
            absence,
            next,
            new PolicyStatusDto(policy?.Version, policy is null || context.Member.HasAccepted(policy.Version), policy?.EffectiveFrom),
            new ClientSettingsDto(
                p[ParameterKey.SamplingMinutes],
                p[ParameterKey.MinGpsAccuracyMeters],
                p[ParameterKey.InactivityAlertMinutes],
                p[ParameterKey.ShiftEndWarningMinutes],
                p[ParameterKey.MaxPauseMinutes],
                p[ParameterKey.AutoCloseGraceMinutes]),
            device,
            overtimeUntil,
            autoCloseAt);
    }

    private async Task<WorkdayDto> ToDtoAsync(Workday workday, DateTimeOffset now, CancellationToken ct)
    {
        var (_, summary) = WorkdayTimeline.Build(workday, now);
        CurrentActivityDto? current = null;

        if (workday.CurrentActivity is { } activity)
        {
            var type = await activityTypes.FindAsync(activity.ActivityTypeId, ct);
            current = new CurrentActivityDto(activity.Id, activity.ActivityTypeId, activity.Name, activity.Color, activity.TimeClass,
                activity.WorkOrderNumber, activity.WorkSiteName, activity.StartedAt, activity.Status,
                (int)activity.WorkedTime(now).TotalSeconds, (int)activity.PausedTime(now).TotalSeconds,
                activity.Status == ActivityStatus.Paused ? activity.Pauses[^1].StartedAt : null,
                activity.StartGeofence, activity.StartLocation.AccuracyMeters,
                new RequirementsDto(type?.RequiresPhoto ?? false, type?.RequiresSignature ?? false, type?.Observation == ObservationPolicy.Always));
        }

        return new WorkdayDto(workday.Id, workday.Date, workday.Status, workday.StartedAt, workday.EndedAt, workday.StartAdjusted,
            workday.LateMinutes, workday.StartGeofence, workday.StartSiteName, workday.CloseKind, workday.Note, current,
            new DaySummaryDto(
                workday.Activities
                    .GroupBy(a => (a.CategoryName, a.Color, a.TimeClass))
                    .Select(g => new CategoryMinutesDto(g.Key.CategoryName, g.Key.Color, g.Key.TimeClass,
                        g.Sum(a => (int)a.WorkedTime(a.EndedAt ?? workday.EndedAt ?? now).TotalMinutes)))
                    .OrderByDescending(c => c.Minutes)
                    .ToList(),
                summary.ActivityCount, summary.ProductiveMinutes, summary.PaidNonProductiveMinutes,
                summary.UnproductiveMinutes, summary.IdleMinutes, summary.WithoutActivityMinutes, summary.PausedMinutes, summary.ElapsedMinutes));
    }

    private static EvidenceDto ToDto(Evidence e) => new(e.Id, e.ActivityRecordId, e.Kind, e.SizeBytes, e.CapturedAt);
}
