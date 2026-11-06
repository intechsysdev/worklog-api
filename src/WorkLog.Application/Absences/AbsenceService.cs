using WorkLog.Application.Abstractions;
using WorkLog.Application.Common;
using WorkLog.Application.Scheduling;
using WorkLog.Domain.Absences;
using WorkLog.Domain.Scheduling;

namespace WorkLog.Application.Absences;

public sealed record AbsenceDto(
    Guid Id,
    Guid MemberId,
    string MemberName,
    AbsenceType Type,
    string TypeLabel,
    DateOnly From,
    DateOnly To,
    TimeOnly? FromTime,
    TimeOnly? ToTime,
    int BusinessDays,
    int? PartialMinutes,
    string? Notes,
    bool HasSupport,
    AbsenceStatus Status,
    string? ResolverName,
    DateTimeOffset? ResolvedAt);

public sealed record RegisterAbsenceCommand(Guid MemberId, AbsenceType Type, DateOnly From, DateOnly To, TimeOnly? FromTime, TimeOnly? ToTime, string? Notes);

public sealed record AbsencePreviewDto(int BusinessDays, int? PartialMinutes, IReadOnlyList<DateOnly> WorkingDates, string Message);

public sealed record UploadSupportCommand(Stream Content, string ContentType, long Length, string FileName);

public interface IAbsenceService
{
    Task<OperationResult<PagedResult<AbsenceDto>>> SearchAsync(AbsenceFilter filter, CancellationToken ct = default);
    Task<OperationResult<AbsencePreviewDto>> PreviewAsync(RegisterAbsenceCommand command, CancellationToken ct = default);
    Task<OperationResult<AbsenceDto>> RegisterAsync(RegisterAbsenceCommand command, CancellationToken ct = default);
    Task<OperationResult<AbsenceDto>> AttachSupportAsync(Guid id, UploadSupportCommand command, CancellationToken ct = default);
    Task<OperationResult<StoredFile>> OpenSupportAsync(Guid id, CancellationToken ct = default);
    Task<OperationResult<AbsenceDto>> ApproveAsync(Guid id, CancellationToken ct = default);
    Task<OperationResult<AbsenceDto>> RejectAsync(Guid id, CancellationToken ct = default);
}

internal sealed class AbsenceService(
    ICurrentActor actor,
    IAbsenceRepository absences,
    IAbsenceQueries queries,
    IMemberRepository members,
    IScheduleCalculator calculator,
    IFileStorage storage,
    IAuditTrail audit,
    IUnitOfWork unitOfWork,
    TimeProvider clock) : IAbsenceService
{
    private const long MaxSupportBytes = 10 * 1024 * 1024;
    private static readonly string[] SupportTypes = ["application/pdf", "image/jpeg", "image/png", "image/webp"];

    public async Task<OperationResult<PagedResult<AbsenceDto>>> SearchAsync(AbsenceFilter filter, CancellationToken ct = default)
    {
        var page = await queries.SearchAsync(filter, ct);
        var context = await ContextAsync(page.Items, ct);
        return page.Map(a => ToDto(a, context));
    }

    public async Task<OperationResult<AbsencePreviewDto>> PreviewAsync(RegisterAbsenceCommand command, CancellationToken ct = default)
    {
        var member = await members.FindAsync(command.MemberId, ct);
        if (member is null) return OperationResult<AbsencePreviewDto>.Invalid("Elige el usuario.", "memberId");

        var probe = Absence.Register(actor.TenantId, command.MemberId, command.Type, command.From, command.To, command.FromTime, command.ToTime, command.Notes);
        if (probe.IsFailure) return OperationResult<AbsencePreviewDto>.From(probe.Error!);

        var plans = (await calculator.PlansAsync([member], command.From, command.To, ct))[member.Id];
        var working = plans.Where(p => p.IsWorkingDay).Select(p => p.Date).ToList();
        int? partial = probe.Value.IsPartialDay ? (int)(command.ToTime!.Value - command.FromTime!.Value).TotalMinutes : null;

        var message = partial is { } minutes
            ? $"Permiso de {ScheduleTemplate.FormatHours(minutes)} el {command.From:dd/MM}."
            : $"Se descontarán {working.Count} día(s) hábil(es). El usuario verá «Hoy no tienes turno» esos días.";
        return new AbsencePreviewDto(partial is null ? working.Count : 0, partial, working, message);
    }

    public async Task<OperationResult<AbsenceDto>> RegisterAsync(RegisterAbsenceCommand command, CancellationToken ct = default)
    {
        if (await members.FindAsync(command.MemberId, ct) is not { IsActive: true } member)
            return OperationResult<AbsenceDto>.Invalid("Elige un usuario activo.", "memberId");

        var overlapping = await queries.ListAsync([command.MemberId], command.From, command.To, ct);
        if (overlapping.Any(a => a.Status != AbsenceStatus.Rejected && !a.IsPartialDay && command.FromTime is null))
            return OperationResult<AbsenceDto>.Conflict("Ya hay una novedad para esas fechas.", "absence.overlap");

        var created = Absence.Register(actor.TenantId, command.MemberId, command.Type, command.From, command.To, command.FromTime, command.ToTime, command.Notes);
        if (created.IsFailure) return OperationResult<AbsenceDto>.From(created.Error!);

        absences.Add(created.Value);
        audit.Record("absence.registered",
            $"Registró {ShiftResolver.AbsenceLabel(command.Type).ToLowerInvariant()} de {member.FullName} del {command.From:dd/MM} al {command.To:dd/MM}.",
            "Novedades", created.Value.Id.ToString());
        await unitOfWork.SaveChangesAsync(ct);
        return ToDto(created.Value, await ContextAsync([created.Value], ct));
    }

    public async Task<OperationResult<AbsenceDto>> AttachSupportAsync(Guid id, UploadSupportCommand command, CancellationToken ct = default)
    {
        var absence = await absences.FindAsync(id, ct);
        if (absence is null) return OperationResult<AbsenceDto>.NotFound("La novedad no existe.");

        if (!SupportTypes.Contains(command.ContentType.ToLowerInvariant()))
            return OperationResult<AbsenceDto>.Invalid("El soporte debe ser PDF o una foto (JPG, PNG, WEBP).", "file");
        if (command.Length is <= 0 or > MaxSupportBytes)
            return OperationResult<AbsenceDto>.Invalid("El soporte supera los 10 MB.", "file");

        var extension = command.ContentType switch
        {
            "application/pdf" => "pdf",
            "image/png" => "png",
            "image/webp" => "webp",
            _ => "jpg"
        };
        var key = $"{actor.TenantId:N}/absences/{absence.Id:N}/{Guid.CreateVersion7():N}.{extension}";

        await storage.SaveAsync(key, command.Content, command.ContentType, ct);
        absence.AttachSupport(key);
        audit.Record("absence.support", "Adjuntó el soporte de una novedad.", "Novedades", absence.Id.ToString());
        await unitOfWork.SaveChangesAsync(ct);
        return ToDto(absence, await ContextAsync([absence], ct));
    }

    public async Task<OperationResult<StoredFile>> OpenSupportAsync(Guid id, CancellationToken ct = default)
    {
        var absence = await absences.FindAsync(id, ct);
        if (absence?.SupportFileKey is not { } key) return OperationResult<StoredFile>.NotFound("La novedad no tiene soporte.");

        var file = await storage.OpenAsync(key, ct);
        return file is null ? OperationResult<StoredFile>.NotFound("El archivo ya no está disponible.") : file;
    }

    public async Task<OperationResult<AbsenceDto>> ApproveAsync(Guid id, CancellationToken ct = default) =>
        await ResolveAsync(id, approve: true, ct);

    public async Task<OperationResult<AbsenceDto>> RejectAsync(Guid id, CancellationToken ct = default) =>
        await ResolveAsync(id, approve: false, ct);

    private async Task<OperationResult<AbsenceDto>> ResolveAsync(Guid id, bool approve, CancellationToken ct)
    {
        var absence = await absences.FindAsync(id, ct);
        if (absence is null) return OperationResult<AbsenceDto>.NotFound("La novedad no existe.");

        var result = approve ? absence.Approve(actor.MemberId, clock.GetUtcNow()) : absence.Reject(actor.MemberId, clock.GetUtcNow());
        if (result.IsFailure) return OperationResult<AbsenceDto>.From(result.Error!);

        var member = await members.FindAsync(absence.MemberId, ct);
        audit.Record(approve ? "absence.approved" : "absence.rejected",
            $"{(approve ? "Aprobó" : "Rechazó")} {ShiftResolver.AbsenceLabel(absence.Type).ToLowerInvariant()} de {member?.FullName}.",
            "Novedades", absence.Id.ToString());
        await unitOfWork.SaveChangesAsync(ct);
        return ToDto(absence, await ContextAsync([absence], ct));
    }

    private sealed record Context(IReadOnlyDictionary<Guid, string> Names, IReadOnlyDictionary<Guid, int> BusinessDays);

    private async Task<Context> ContextAsync(IReadOnlyCollection<Absence> list, CancellationToken ct)
    {
        var ids = list.SelectMany(a => new[] { a.MemberId, a.ResolvedBy ?? Guid.Empty }).Where(i => i != Guid.Empty).Distinct().ToList();
        var people = await members.FindManyAsync(ids, ct);
        var days = new Dictionary<Guid, int>();

        if (list.Count > 0)
        {
            var subjects = people.Where(p => list.Any(a => a.MemberId == p.Id)).ToList();
            var plans = await calculator.PlansAsync(subjects, list.Min(a => a.From), list.Max(a => a.To), ct);

            // Días hábiles = días con turno en el horario base (sin la novedad): se cuentan con el plan
            // calculado, y una novedad aprobada aparece como "Absence" en vez de "Shift".
            foreach (var absence in list.Where(a => plans.ContainsKey(a.MemberId)))
            {
                days[absence.Id] = plans[absence.MemberId].Count(p => p.Date >= absence.From && p.Date <= absence.To &&
                                                                     (p.IsWorkingDay || p.Kind == DayKind.Absence));
            }
        }

        return new Context(people.ToDictionary(p => p.Id, p => p.FullName), days);
    }

    private static AbsenceDto ToDto(Absence a, Context c) => new(
        a.Id, a.MemberId, c.Names.GetValueOrDefault(a.MemberId, string.Empty), a.Type, ShiftResolver.AbsenceLabel(a.Type),
        a.From, a.To, a.FromTime, a.ToTime, a.IsPartialDay ? 0 : c.BusinessDays.GetValueOrDefault(a.Id),
        a.IsPartialDay ? (int)(a.ToTime!.Value - a.FromTime!.Value).TotalMinutes : null,
        a.Notes, a.SupportFileKey is not null, a.Status,
        a.ResolvedBy is { } by ? c.Names.GetValueOrDefault(by) : null, a.ResolvedAt);
}
