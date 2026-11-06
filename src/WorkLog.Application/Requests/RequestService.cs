using WorkLog.Application.Abstractions;
using WorkLog.Application.Common;
using WorkLog.Application.Parameters;
using WorkLog.Application.Scheduling;
using WorkLog.Domain.Access;
using WorkLog.Domain.Common;
using WorkLog.Domain.Parameters;
using WorkLog.Domain.Requests;
using WorkLog.Domain.Scheduling;
using WorkLog.Domain.Workdays;

namespace WorkLog.Application.Requests;

public sealed record ShiftChangeDayDto(DateOnly Date, TimeOnly? Start, TimeOnly? End, int LunchMinutes);

public sealed record RequestDto(
    Guid Id,
    Guid MemberId,
    string MemberName,
    RequestType Type,
    RequestStatus Status,
    string Title,
    string Detail,
    string Reason,
    DateOnly Date,
    DateTimeOffset SubmittedAt,
    Guid? WorkdayId,
    Guid? ActivityRecordId,
    AdjustmentTarget? Target,
    DateTimeOffset? OriginalValue,
    DateTimeOffset? ProposedValue,
    TimeOnly? OvertimeFrom,
    TimeOnly? OvertimeTo,
    string? AuthorizationReference,
    IReadOnlyList<ShiftChangeDayDto> Days,
    string? ResolverName,
    DateTimeOffset? ResolvedAt,
    string? ResolverComment);

public sealed record RequestDetailDto(RequestDto Request, IReadOnlyList<string> Effects, IReadOnlyList<string> Warnings);

public sealed record SubmitAdjustmentCommand(Guid WorkdayId, Guid? ActivityRecordId, AdjustmentTarget Target, DateTimeOffset ProposedValue, string? Reason);

public sealed record SubmitOvertimeCommand(DateOnly Date, TimeOnly From, TimeOnly To, string? AuthorizationReference, string? Reason);

public sealed record SubmitShiftChangeCommand(IReadOnlyList<ShiftChangeDayDto>? Days, string? Reason);

public sealed record ResolveRequestCommand(string? Comment);

public interface IRequestService
{
    // App móvil
    Task<OperationResult<RequestDto>> SubmitAdjustmentAsync(SubmitAdjustmentCommand command, CancellationToken ct = default);
    Task<OperationResult<RequestDto>> SubmitOvertimeAsync(SubmitOvertimeCommand command, CancellationToken ct = default);
    Task<OperationResult<RequestDto>> SubmitShiftChangeAsync(SubmitShiftChangeCommand command, CancellationToken ct = default);
    Task<OperationResult<PagedResult<RequestDto>>> ListMineAsync(RequestStatus? status, PageRequest page, CancellationToken ct = default);
    Task<OperationResult<RequestDto>> CancelAsync(Guid id, CancellationToken ct = default);

    // Panel
    Task<OperationResult<PagedResult<RequestDto>>> SearchAsync(RequestType? type, RequestStatus? status, Guid? branchId, PageRequest page, CancellationToken ct = default);
    Task<OperationResult<RequestDetailDto>> GetAsync(Guid id, CancellationToken ct = default);
    Task<int> CountPendingAsync(CancellationToken ct = default);
    Task<OperationResult<RequestDetailDto>> ApproveAsync(Guid id, ResolveRequestCommand command, CancellationToken ct = default);
    Task<OperationResult<RequestDetailDto>> RejectAsync(Guid id, ResolveRequestCommand command, CancellationToken ct = default);
}

internal sealed class RequestService(
    ICurrentActor actor,
    IWorkRequestRepository requests,
    IMemberRepository members,
    IBranchRepository branches,
    IWorkdayRepository workdays,
    IShiftOverrideRepository overrides,
    IPayrollPeriodRepository periods,
    IScheduleCalculator calculator,
    IParameterProvider parameters,
    IAuditTrail audit,
    IUnitOfWork unitOfWork,
    TimeProvider clock) : IRequestService
{
    // ── App móvil ───────────────────────────────────────────────────────────

    public async Task<OperationResult<RequestDto>> SubmitAdjustmentAsync(SubmitAdjustmentCommand command, CancellationToken ct = default)
    {
        var workday = await workdays.FindAsync(command.WorkdayId, ct);
        if (workday is null || workday.MemberId != actor.MemberId)
            return OperationResult<RequestDto>.NotFound("La jornada no existe.");

        DateTimeOffset? original = command.Target switch
        {
            AdjustmentTarget.WorkdayStart => workday.StartedAt,
            AdjustmentTarget.WorkdayEnd => workday.EndedAt,
            _ => workday.Activities.FirstOrDefault(a => a.Id == command.ActivityRecordId) is { } a
                ? command.Target == AdjustmentTarget.ActivityStart ? a.StartedAt : a.EndedAt
                : null
        };

        if (command.Target is AdjustmentTarget.ActivityStart or AdjustmentTarget.ActivityEnd &&
            workday.Activities.All(a => a.Id != command.ActivityRecordId))
            return OperationResult<RequestDto>.Invalid("La actividad no pertenece a esa jornada.", "activityRecordId");

        if (await IsClosedAsync(actor.MemberId, workday.Date, ct))
            return OperationResult<RequestDto>.Conflict("El periodo de esa fecha ya está cerrado: el ajuste lo hace tu supervisor en el periodo siguiente.", "period.closed");

        var created = WorkRequest.Adjustment(actor.TenantId, actor.MemberId, workday.Date, workday.Id, command.ActivityRecordId,
            command.Target, original, command.ProposedValue, command.Reason, clock.GetUtcNow());
        return await SaveNewAsync(created, ct);
    }

    public async Task<OperationResult<RequestDto>> SubmitOvertimeAsync(SubmitOvertimeCommand command, CancellationToken ct = default)
    {
        var created = WorkRequest.OvertimeRequest(actor.TenantId, actor.MemberId, command.Date, command.From, command.To,
            command.AuthorizationReference, command.Reason, clock.GetUtcNow());
        return await SaveNewAsync(created, ct);
    }

    public async Task<OperationResult<RequestDto>> SubmitShiftChangeAsync(SubmitShiftChangeCommand command, CancellationToken ct = default)
    {
        var member = await members.FindAsync(actor.MemberId, ct);
        var today = await TodayForAsync(member, ct);
        var days = (command.Days ?? []).Select(d => new ShiftChangeDay(d.Date, d.Start, d.End, d.LunchMinutes)).ToList();

        if (days.Any(d => d.Date < today))
            return OperationResult<RequestDto>.Invalid("Los cambios de turno son para hoy o días futuros.", "days");

        var created = WorkRequest.ShiftChangeRequest(actor.TenantId, actor.MemberId, days, command.Reason, clock.GetUtcNow());
        return await SaveNewAsync(created, ct);
    }

    public async Task<OperationResult<PagedResult<RequestDto>>> ListMineAsync(RequestStatus? status, PageRequest page, CancellationToken ct = default)
    {
        var result = await requests.SearchAsync(new RequestFilter(page, [actor.MemberId], null, status), ct);
        var names = await NamesAsync(result.Items, ct);
        return result.Map(r => ToDto(r, names));
    }

    public async Task<OperationResult<RequestDto>> CancelAsync(Guid id, CancellationToken ct = default)
    {
        var request = await requests.FindAsync(id, ct);
        if (request is null || request.MemberId != actor.MemberId) return OperationResult<RequestDto>.NotFound("La solicitud no existe.");

        var cancelled = request.Cancel(actor.MemberId, clock.GetUtcNow());
        if (cancelled.IsFailure) return OperationResult<RequestDto>.From(cancelled.Error!);

        await unitOfWork.SaveChangesAsync(ct);
        return ToDto(request, await NamesAsync([request], ct));
    }

    // ── Panel ───────────────────────────────────────────────────────────────

    public async Task<OperationResult<PagedResult<RequestDto>>> SearchAsync(RequestType? type, RequestStatus? status, Guid? branchId,
        PageRequest page, CancellationToken ct = default)
    {
        IReadOnlyCollection<Guid>? scope = null;
        if (branchId is { } b) scope = (await members.ListMobileAsync(b, ct)).Select(m => m.Id).ToList();

        var result = await requests.SearchAsync(new RequestFilter(page, scope, type, status), ct);
        var names = await NamesAsync(result.Items, ct);
        return result.Map(r => ToDto(r, names));
    }

    public Task<int> CountPendingAsync(CancellationToken ct = default) => requests.CountPendingAsync(ct);

    public async Task<OperationResult<RequestDetailDto>> GetAsync(Guid id, CancellationToken ct = default)
    {
        var request = await requests.FindAsync(id, ct);
        if (request is null) return OperationResult<RequestDetailDto>.NotFound("La solicitud no existe.");

        // El impacto se calcula aplicando el cambio en memoria. Esta consulta nunca guarda, así que
        // lo aplicado se descarta al terminar la petición.
        var (effects, warnings) = await ImpactAsync(request, ct);
        return new RequestDetailDto(ToDto(request, await NamesAsync([request], ct)), effects, warnings);
    }

    public async Task<OperationResult<RequestDetailDto>> ApproveAsync(Guid id, ResolveRequestCommand command, CancellationToken ct = default)
    {
        var request = await requests.FindAsync(id, ct);
        if (request is null) return OperationResult<RequestDetailDto>.NotFound("La solicitud no existe.");

        if (request.IsPending && await IsClosedAsync(request.MemberId, request.Date, ct))
            return OperationResult<RequestDetailDto>.Conflict("El periodo de esa fecha ya está cerrado. Reábrelo o registra el ajuste en el periodo siguiente.", "period.closed");

        var approved = request.Approve(actor.MemberId, command.Comment, clock.GetUtcNow());
        if (approved.IsFailure) return OperationResult<RequestDetailDto>.From(approved.Error!);

        var effect = await ApplyAsync(request, ct);
        if (effect.IsFailure) return OperationResult<RequestDetailDto>.From(effect.Error!);

        var member = await members.FindAsync(request.MemberId, ct);
        audit.Record("request.approved", $"Aprobó {Title(request).ToLowerInvariant()} de {member?.FullName}: {Detail(request)}.",
            "Solicitudes", request.Id.ToString(), new { request.Status }, new { Status = RequestStatus.Approved, command.Comment });

        await unitOfWork.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    public async Task<OperationResult<RequestDetailDto>> RejectAsync(Guid id, ResolveRequestCommand command, CancellationToken ct = default)
    {
        var request = await requests.FindAsync(id, ct);
        if (request is null) return OperationResult<RequestDetailDto>.NotFound("La solicitud no existe.");

        var rejected = request.Reject(actor.MemberId, command.Comment, clock.GetUtcNow());
        if (rejected.IsFailure) return OperationResult<RequestDetailDto>.From(rejected.Error!);

        var member = await members.FindAsync(request.MemberId, ct);
        audit.Record("request.rejected", $"Rechazó {Title(request).ToLowerInvariant()} de {member?.FullName}: {command.Comment}.",
            "Solicitudes", request.Id.ToString());

        await unitOfWork.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    // ── Efectos ─────────────────────────────────────────────────────────────

    private async Task<Result> ApplyAsync(WorkRequest request, CancellationToken ct)
    {
        switch (request.Type)
        {
            case RequestType.RecordAdjustment:
                var workday = await workdays.FindAsync(request.WorkdayId!.Value, ct);
                if (workday is null) return DomainError.NotFound("workday.not_found", "La jornada ya no existe.");
                return workday.ApplyAdjustment(request.Target!.Value, request.ActivityRecordId, request.ProposedValue!.Value);

            case RequestType.Overtime:
                var limits = await OvertimeUsageAsync(request, ct);
                if (limits.DayMinutes + request.OvertimeMinutes > limits.DailyMax)
                    return DomainError.Validation("overtime.daily_max", $"Supera el máximo de {limits.DailyMax / 60} h extra al día.");
                if (limits.WeekMinutes + request.OvertimeMinutes > limits.WeeklyMax)
                    return DomainError.Validation("overtime.weekly_max", $"Supera el máximo de {limits.WeeklyMax / 60} h extra a la semana.");
                return Result.Success();

            case RequestType.ShiftChange:
                var dates = request.Days.Select(d => d.Date).ToList();
                foreach (var existing in await overrides.ListAsync([request.MemberId], dates.Min(), dates.Max(), ct))
                {
                    if (dates.Contains(existing.Date)) overrides.Remove(existing);
                }

                foreach (var day in request.Days)
                {
                    var created = ShiftOverride.Create(actor.TenantId, request.MemberId, day.Date, day.Start, day.End, day.LunchMinutes,
                        request.Reason, request.Id);
                    if (created.IsFailure) return created.Error!;
                    overrides.Add(created.Value);
                }

                return Result.Success();

            default:
                return Result.Success();
        }
    }

    private sealed record OvertimeLimits(int DayMinutes, int WeekMinutes, int DailyMax, int WeeklyMax);

    private async Task<OvertimeLimits> OvertimeUsageAsync(WorkRequest request, CancellationToken ct)
    {
        var member = await members.FindAsync(request.MemberId, ct);
        var branch = member?.BranchId is { } id ? await branches.FindAsync(id, ct) : null;
        var p = await parameters.ForBranchAsync(branch, request.Date, ct);
        var monday = LocalTime.StartOfWeek(request.Date);

        var approved = (await requests.ListAsync([request.MemberId], monday, monday.AddDays(6), RequestStatus.Approved, ct))
            .Where(r => r.Type == RequestType.Overtime && r.Id != request.Id)
            .ToList();

        return new OvertimeLimits(
            approved.Where(r => r.Date == request.Date).Sum(r => r.OvertimeMinutes),
            approved.Sum(r => r.OvertimeMinutes),
            p[ParameterKey.OvertimeDailyMaxHours] * 60,
            p[ParameterKey.OvertimeWeeklyMaxHours] * 60);
    }

    private async Task<(IReadOnlyList<string> Effects, IReadOnlyList<string> Warnings)> ImpactAsync(WorkRequest request, CancellationToken ct)
    {
        var effects = new List<string>();
        var warnings = new List<string>();
        var now = clock.GetUtcNow();

        if (await IsClosedAsync(request.MemberId, request.Date, ct))
            warnings.Add("El periodo de esa fecha está cerrado.");

        switch (request.Type)
        {
            case RequestType.RecordAdjustment when request.WorkdayId is { } workdayId && await workdays.FindAsync(workdayId, ct) is { } workday:
                var before = WorkdayTimeline.Build(workday, now).Summary;
                if (request.IsPending)
                {
                    var simulated = workday.ApplyAdjustment(request.Target!.Value, request.ActivityRecordId, request.ProposedValue!.Value);
                    if (simulated.IsFailure)
                    {
                        warnings.Add(simulated.Error!.Message);
                        break;
                    }
                }

                var after = WorkdayTimeline.Build(workday, now).Summary;
                effects.Add($"Productivo {Hours(before.ProductiveMinutes)} → {Hours(after.ProductiveMinutes)}");
                effects.Add($"Sin actividad {Hours(before.WithoutActivityMinutes)} → {Hours(after.WithoutActivityMinutes)}");
                effects.Add($"Total del día {Hours(before.WorkedMinutes)} → {Hours(after.WorkedMinutes)}");
                break;

            case RequestType.Overtime:
                var limits = await OvertimeUsageAsync(request, ct);
                var used = request.Status == RequestStatus.Approved ? 0 : request.OvertimeMinutes;
                effects.Add($"Extra en el día: {Hours(limits.DayMinutes + used)} de {Hours(limits.DailyMax)}");
                effects.Add($"Extra en la semana: {Hours(limits.WeekMinutes + used)} de {Hours(limits.WeeklyMax)}");
                if (string.IsNullOrWhiteSpace(request.AuthorizationReference))
                    warnings.Add("No trae número de autorización de horas extra.");
                if (limits.DayMinutes + used > limits.DailyMax || limits.WeekMinutes + used > limits.WeeklyMax)
                    warnings.Add("Supera el máximo legal de horas extra.");
                break;

            case RequestType.ShiftChange:
                if (await members.FindAsync(request.MemberId, ct) is not { } person) break;
                var monday = LocalTime.StartOfWeek(request.Date);
                var plans = (await calculator.PlansAsync([person], monday, monday.AddDays(6), ct))[person.Id];
                var current = plans.Sum(p => p.ScheduledMinutes);
                var proposed = plans.Sum(p => request.Days.FirstOrDefault(d => d.Date == p.Date) is { } d
                    ? d.Start is { } s && d.End is { } e ? (int)(e - s).TotalMinutes - d.LunchMinutes : 0
                    : p.ScheduledMinutes);
                effects.Add($"Semana programada: {Hours(current)} → {Hours(proposed)}");
                var cap = (await parameters.ForBranchAsync(null, request.Date, ct))[ParameterKey.WeeklyCapHours] * 60;
                if (proposed > cap) warnings.Add($"La semana pasaría el tope de {Hours(cap)}: el excedente sería extra.");
                break;
        }

        return (effects, warnings);
    }

    // ── Apoyo ───────────────────────────────────────────────────────────────

    private async Task<OperationResult<RequestDto>> SaveNewAsync(Result<WorkRequest> created, CancellationToken ct)
    {
        if (created.IsFailure) return OperationResult<RequestDto>.From(created.Error!);

        requests.Add(created.Value);
        audit.Record("request.submitted", $"Envió {Title(created.Value).ToLowerInvariant()}: {Detail(created.Value)}.", "Solicitudes",
            created.Value.Id.ToString());
        await unitOfWork.SaveChangesAsync(ct);
        return ToDto(created.Value, await NamesAsync([created.Value], ct));
    }

    private async Task<bool> IsClosedAsync(Guid memberId, DateOnly date, CancellationToken ct)
    {
        var member = await members.FindAsync(memberId, ct);
        return member?.BranchId is { } branchId && await periods.IsClosedAsync(branchId, date, ct);
    }

    private async Task<DateOnly> TodayForAsync(Member? member, CancellationToken ct)
    {
        var branch = member?.BranchId is { } id ? await branches.FindAsync(id, ct) : null;
        return LocalTime.Today(clock, LocalTime.Find(branch?.TimeZoneId));
    }

    private async Task<IReadOnlyDictionary<Guid, string>> NamesAsync(IEnumerable<WorkRequest> list, CancellationToken ct)
    {
        var ids = list.SelectMany(r => new[] { r.MemberId, r.ResolvedBy ?? Guid.Empty }).Where(i => i != Guid.Empty).Distinct().ToList();
        return (await members.FindManyAsync(ids, ct)).ToDictionary(m => m.Id, m => m.FullName);
    }

    private static string Title(WorkRequest r) => r.Type switch
    {
        RequestType.RecordAdjustment => "Ajuste de registro",
        RequestType.Overtime => "Horas extra",
        _ => "Cambio de turno"
    };

    private static string Detail(WorkRequest r) => r.Type switch
    {
        RequestType.RecordAdjustment =>
            $"{TargetLabel(r.Target)} {(r.OriginalValue is { } o ? Local(o) : "—")} → {(r.ProposedValue is { } p ? Local(p) : "—")} del {r.Date:dd/MM}",
        RequestType.Overtime => $"{r.OvertimeFrom:HH\\:mm}–{r.OvertimeTo:HH\\:mm} el {r.Date:dd/MM} ({Hours(r.OvertimeMinutes)})",
        _ => string.Join(" y ", r.Days.Select(d => d.Start is null ? $"{d.Date:dd/MM} libre" : $"{d.Date:dd/MM} {d.Start:HH\\:mm}–{d.End:HH\\:mm}"))
    };

    /// <summary>Hora local para el texto (la base guarda instantes en UTC).</summary>
    private static string Local(DateTimeOffset instant) =>
        TimeZoneInfo.ConvertTime(instant, LocalTime.Find(null)).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    private static string TargetLabel(AdjustmentTarget? target) => target switch
    {
        AdjustmentTarget.WorkdayStart => "Inicio de jornada",
        AdjustmentTarget.WorkdayEnd => "Fin de jornada",
        AdjustmentTarget.ActivityStart => "Inicio",
        _ => "Fin"
    };

    private static string Hours(int minutes) => ScheduleTemplate.FormatHours(minutes);

    private static RequestDto ToDto(WorkRequest r, IReadOnlyDictionary<Guid, string> names) => new(
        r.Id, r.MemberId, names.GetValueOrDefault(r.MemberId, string.Empty), r.Type, r.Status, Title(r), Detail(r), r.Reason, r.Date,
        r.SubmittedAt, r.WorkdayId, r.ActivityRecordId, r.Target, r.OriginalValue, r.ProposedValue, r.OvertimeFrom, r.OvertimeTo,
        r.AuthorizationReference, r.Days.Select(d => new ShiftChangeDayDto(d.Date, d.Start, d.End, d.LunchMinutes)).ToList(),
        r.ResolvedBy is { } by ? names.GetValueOrDefault(by) : null, r.ResolvedAt, r.ResolverComment);
}
