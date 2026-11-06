using WorkLog.Application.Abstractions;
using WorkLog.Application.Common;
using WorkLog.Application.Parameters;
using WorkLog.Domain.Common;
using WorkLog.Domain.Scheduling;

namespace WorkLog.Application.Scheduling;

public sealed record ScheduleCheckDto(string Code, CheckSeverity Severity, string Message);

public sealed record ScheduleTemplateDto(
    Guid Id,
    string Name,
    WeekDays Days,
    string DayLetters,
    TimeOnly StartTime,
    TimeOnly EndTime,
    int LunchMinutes,
    int DailyMinutes,
    int WeeklyMinutes,
    string Summary,
    bool IsActive,
    bool IsCompliant,
    IReadOnlyList<ScheduleCheckDto> Checks,
    int People,
    int Assignments);

public sealed record SaveTemplateCommand(string? Name, WeekDays Days, TimeOnly StartTime, TimeOnly EndTime, int LunchMinutes);

public sealed record TemplatePreviewDto(int DailyMinutes, int WeeklyMinutes, bool IsCompliant, IReadOnlyList<ScheduleCheckDto> Checks);

public sealed record AssignmentDto(
    Guid Id,
    AssignmentLevel Level,
    Guid TargetId,
    string TargetName,
    Guid TemplateId,
    string TemplateName,
    DateOnly EffectiveFrom,
    DateTimeOffset CreatedAt);

public sealed record CreateAssignmentCommand(AssignmentLevel Level, Guid TargetId, Guid TemplateId, DateOnly? EffectiveFrom);

public sealed record CalendarDayDto(
    DateOnly Date,
    DayKind Kind,
    TimeOnly? Start,
    TimeOnly? End,
    int ScheduledMinutes,
    string? Label,
    string? TemplateName);

public sealed record MemberCalendarDto(
    Guid MemberId,
    string MemberName,
    int Year,
    int Month,
    int ScheduledMinutes,
    string? SourceDescription,
    IReadOnlyList<CalendarDayDto> Days);

public interface IScheduleService
{
    Task<IReadOnlyList<ScheduleTemplateDto>> ListTemplatesAsync(CancellationToken ct = default);
    Task<TemplatePreviewDto> PreviewTemplateAsync(SaveTemplateCommand command, CancellationToken ct = default);
    Task<OperationResult<ScheduleTemplateDto>> CreateTemplateAsync(SaveTemplateCommand command, CancellationToken ct = default);
    Task<OperationResult<ScheduleTemplateDto>> UpdateTemplateAsync(Guid id, SaveTemplateCommand command, CancellationToken ct = default);
    Task<OperationResult<ScheduleTemplateDto>> SetTemplateActiveAsync(Guid id, bool active, CancellationToken ct = default);

    Task<IReadOnlyList<AssignmentDto>> ListAssignmentsAsync(CancellationToken ct = default);
    Task<OperationResult<AssignmentDto>> CreateAssignmentAsync(CreateAssignmentCommand command, CancellationToken ct = default);
    Task<OperationResult> DeleteAssignmentAsync(Guid id, CancellationToken ct = default);

    Task<OperationResult<MemberCalendarDto>> GetCalendarAsync(Guid memberId, int year, int month, CancellationToken ct = default);
}

internal sealed class ScheduleService(
    ICurrentActor actor,
    IScheduleTemplateRepository templates,
    IScheduleAssignmentRepository assignments,
    IMemberRepository members,
    IWorkGroupRepository groups,
    IBranchRepository branches,
    IScheduleCalculator calculator,
    IParameterProvider parameters,
    IAuditTrail audit,
    IUnitOfWork unitOfWork,
    TimeProvider clock) : IScheduleService
{
    // ── Plantillas ──────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<ScheduleTemplateDto>> ListTemplatesAsync(CancellationToken ct = default)
    {
        var rules = await parameters.LaborRulesAsync(Today(), ct);
        var list = await templates.ListAsync(ct);
        var usage = await UsageAsync(ct);

        return list
            .OrderBy(t => !t.IsActive)
            .ThenBy(t => t.Name)
            .Select(t => ToDto(t, rules, usage.People.GetValueOrDefault(t.Id), usage.Assignments.GetValueOrDefault(t.Id)))
            .ToList();
    }

    public async Task<TemplatePreviewDto> PreviewTemplateAsync(SaveTemplateCommand command, CancellationToken ct = default)
    {
        var rules = await parameters.LaborRulesAsync(Today(), ct);
        var checks = ScheduleTemplate.Evaluate(command.Days, command.StartTime, command.EndTime, command.LunchMinutes, rules);
        var daily = (int)(command.EndTime - command.StartTime).TotalMinutes - command.LunchMinutes;

        return new TemplatePreviewDto(daily, daily * command.Days.CountDays(), checks.All(c => c.Severity != CheckSeverity.Fail),
            checks.Select(c => new ScheduleCheckDto(c.Code, c.Severity, c.Message)).ToList());
    }

    public async Task<OperationResult<ScheduleTemplateDto>> CreateTemplateAsync(SaveTemplateCommand command, CancellationToken ct = default)
    {
        if (await templates.NameExistsAsync(Text.Key(command.Name), null, ct))
            return OperationResult<ScheduleTemplateDto>.Invalid("Ya existe una plantilla con ese nombre.", "name", "template.name_taken");

        var rules = await parameters.LaborRulesAsync(Today(), ct);
        var created = ScheduleTemplate.Create(actor.TenantId, ToData(command), rules);
        if (created.IsFailure) return OperationResult<ScheduleTemplateDto>.From(created.Error!);

        templates.Add(created.Value);
        audit.Record("template.created", $"Creó la plantilla {created.Value.Name} ({created.Value.Summary}).", "Horarios",
            created.Value.Id.ToString(), after: new { created.Value.Summary });
        await unitOfWork.SaveChangesAsync(ct);
        return ToDto(created.Value, rules, 0, 0);
    }

    public async Task<OperationResult<ScheduleTemplateDto>> UpdateTemplateAsync(Guid id, SaveTemplateCommand command, CancellationToken ct = default)
    {
        var template = await templates.FindAsync(id, ct);
        if (template is null) return OperationResult<ScheduleTemplateDto>.NotFound("La plantilla no existe.");

        if (await templates.NameExistsAsync(Text.Key(command.Name), id, ct))
            return OperationResult<ScheduleTemplateDto>.Invalid("Ya existe una plantilla con ese nombre.", "name", "template.name_taken");

        var rules = await parameters.LaborRulesAsync(Today(), ct);
        var before = template.Summary;
        var updated = template.Update(ToData(command), rules);
        if (updated.IsFailure) return OperationResult<ScheduleTemplateDto>.From(updated.Error!);

        audit.Record("template.updated", $"Cambió la plantilla {template.Name}: {before} → {template.Summary}.", "Horarios",
            template.Id.ToString(), new { Summary = before }, new { template.Summary });
        await unitOfWork.SaveChangesAsync(ct);

        var usage = await UsageAsync(ct);
        return ToDto(template, rules, usage.People.GetValueOrDefault(id), usage.Assignments.GetValueOrDefault(id));
    }

    public async Task<OperationResult<ScheduleTemplateDto>> SetTemplateActiveAsync(Guid id, bool active, CancellationToken ct = default)
    {
        var template = await templates.FindAsync(id, ct);
        if (template is null) return OperationResult<ScheduleTemplateDto>.NotFound("La plantilla no existe.");

        var rules = await parameters.LaborRulesAsync(Today(), ct);
        if (active)
        {
            var result = template.Activate(rules);
            if (result.IsFailure) return OperationResult<ScheduleTemplateDto>.From(result.Error!);
        }
        else
        {
            template.Deactivate();
        }

        audit.Record(active ? "template.activated" : "template.deactivated",
            $"{(active ? "Activó" : "Desactivó")} la plantilla {template.Name}.", "Horarios", template.Id.ToString());
        await unitOfWork.SaveChangesAsync(ct);

        var usage = await UsageAsync(ct);
        return ToDto(template, rules, usage.People.GetValueOrDefault(id), usage.Assignments.GetValueOrDefault(id));
    }

    // ── Asignaciones ────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<AssignmentDto>> ListAssignmentsAsync(CancellationToken ct = default)
    {
        var list = await assignments.ListAsync(ct);
        var names = await TargetNamesAsync(ct);
        var templateNames = (await templates.ListAsync(ct)).ToDictionary(t => t.Id, t => t.Name);

        return list
            .OrderByDescending(a => a.EffectiveFrom)
            .ThenByDescending(a => a.Level)
            .Select(a => ToDto(a, names, templateNames))
            .ToList();
    }

    public async Task<OperationResult<AssignmentDto>> CreateAssignmentAsync(CreateAssignmentCommand command, CancellationToken ct = default)
    {
        if (command.EffectiveFrom is not { } from)
            return OperationResult<AssignmentDto>.Invalid("Elige la fecha de inicio.", "effectiveFrom");

        var template = await templates.FindAsync(command.TemplateId, ct);
        if (template is not { IsActive: true }) return OperationResult<AssignmentDto>.Invalid("Elige un horario activo.", "templateId");

        var targetExists = command.Level switch
        {
            AssignmentLevel.Member => await members.FindAsync(command.TargetId, ct) is not null,
            AssignmentLevel.Group => await groups.FindAsync(command.TargetId, ct) is { IsActive: true },
            _ => await branches.FindAsync(command.TargetId, ct) is { IsActive: true }
        };

        if (!targetExists)
        {
            var message = command.Level switch
            {
                AssignmentLevel.Member => "Elige la persona.",
                AssignmentLevel.Group => "Elige el grupo.",
                _ => "Elige la sede."
            };
            return OperationResult<AssignmentDto>.Invalid(message, "targetId");
        }

        if (await assignments.ExistsAsync(command.Level, command.TargetId, from, ct))
            return OperationResult<AssignmentDto>.Conflict("Ya existe una asignación igual desde esa fecha.", "assignment.duplicate");

        var created = ScheduleAssignment.Create(actor.TenantId, command.Level, command.TargetId, command.TemplateId, from);
        if (created.IsFailure) return OperationResult<AssignmentDto>.From(created.Error!);

        assignments.Add(created.Value);
        var names = await TargetNamesAsync(ct);
        var targetName = names.GetValueOrDefault(created.Value.TargetId, "—");
        audit.Record("assignment.created", $"Asignó {template.Name} a {targetName} desde el {from:dd/MM/yyyy}.", "Horarios",
            created.Value.Id.ToString(), after: new { command.Level, targetName, template.Name, from });
        await unitOfWork.SaveChangesAsync(ct);

        return ToDto(created.Value, names, new Dictionary<Guid, string> { [template.Id] = template.Name });
    }

    public async Task<OperationResult> DeleteAssignmentAsync(Guid id, CancellationToken ct = default)
    {
        var assignment = await assignments.FindAsync(id, ct);
        if (assignment is null) return OperationResult.NotFound("La asignación no existe.");

        // Borrar una asignación que ya empezó reescribiría el turno de días pasados.
        if (assignment.EffectiveFrom <= Today())
            return OperationResult.Conflict("La asignación ya está vigente: crea otra desde una fecha nueva para reemplazarla.", "assignment.in_effect");

        assignments.Remove(assignment);
        audit.Record("assignment.deleted", "Eliminó una asignación futura.", "Horarios", id.ToString(),
            new { assignment.Level, assignment.TargetId, assignment.TemplateId, assignment.EffectiveFrom });
        await unitOfWork.SaveChangesAsync(ct);
        return OperationResult.Ok();
    }

    // ── Calendario ──────────────────────────────────────────────────────────

    public async Task<OperationResult<MemberCalendarDto>> GetCalendarAsync(Guid memberId, int year, int month, CancellationToken ct = default)
    {
        if (year is < 2020 or > 2100 || month is < 1 or > 12)
            return OperationResult<MemberCalendarDto>.Invalid("El mes no es válido.", "month");

        var member = await members.FindAsync(memberId, ct);
        if (member is null) return OperationResult<MemberCalendarDto>.NotFound("La persona no existe.");

        var first = new DateOnly(year, month, 1);
        var last = first.AddMonths(1).AddDays(-1);
        var plans = (await calculator.PlansAsync([member], first, last, ct))[member.Id];

        var names = await TargetNamesAsync(ct);
        var source = plans
            .Where(p => p.Source is not null)
            .GroupBy(p => (p.Source, p.SourceTargetId, p.TemplateName))
            .Select(g => $"{LevelLabel(g.Key.Source!.Value)} {names.GetValueOrDefault(g.Key.SourceTargetId ?? Guid.Empty, "—")} ({g.Key.TemplateName})")
            .ToList();

        return new MemberCalendarDto(
            member.Id,
            member.FullName,
            year,
            month,
            plans.Sum(p => p.ScheduledMinutes),
            source.Count == 0 ? null : string.Join(" · ", source),
            plans.Select(p => new CalendarDayDto(p.Date, p.Kind, p.Start, p.End, p.ScheduledMinutes, p.Label, p.TemplateName)).ToList());
    }

    // ── Apoyo ───────────────────────────────────────────────────────────────

    private sealed record Usage(IReadOnlyDictionary<Guid, int> People, IReadOnlyDictionary<Guid, int> Assignments);

    /// <summary>Cuántas personas usan hoy cada plantilla y en cuántas asignaciones aparece.</summary>
    private async Task<Usage> UsageAsync(CancellationToken ct)
    {
        var assignmentList = await assignments.ListAsync(ct);
        var people = (await members.ListAllAsync(ct)).Where(m => m.Status != Domain.Access.MemberStatus.Inactive).ToList();
        var today = Today();
        var plans = await calculator.PlansAsync(people, today, today, ct);

        return new Usage(
            plans.Values.Select(p => p[0].TemplateId).Where(t => t is not null).GroupBy(t => t!.Value).ToDictionary(g => g.Key, g => g.Count()),
            assignmentList.GroupBy(a => a.TemplateId).ToDictionary(g => g.Key, g => g.Count()));
    }

    private async Task<IReadOnlyDictionary<Guid, string>> TargetNamesAsync(CancellationToken ct)
    {
        var names = new Dictionary<Guid, string>();
        foreach (var branch in await branches.ListAsync(ct)) names[branch.Id] = branch.Name;
        foreach (var group in await groups.ListAsync(null, ct)) names[group.Id] = group.Name;
        foreach (var member in await members.ListAllAsync(ct)) names[member.Id] = member.FullName;
        return names;
    }

    private static string LevelLabel(AssignmentLevel level) => level switch
    {
        AssignmentLevel.Member => "Persona",
        AssignmentLevel.Group => "Grupo",
        _ => "Sede"
    };

    private DateOnly Today() => LocalTime.Today(clock, LocalTime.Find(null));

    private static ScheduleTemplateData ToData(SaveTemplateCommand c) =>
        new(c.Name ?? string.Empty, c.Days, c.StartTime, c.EndTime, c.LunchMinutes);

    private static ScheduleTemplateDto ToDto(ScheduleTemplate t, LaborRules rules, int people, int assignmentCount)
    {
        var checks = t.Evaluate(rules);
        return new ScheduleTemplateDto(t.Id, t.Name, t.Days, t.Days.ToLetters(), t.StartTime, t.EndTime, t.LunchMinutes,
            t.DailyMinutes, t.WeeklyMinutes, t.Summary, t.IsActive, checks.All(c => c.Severity != CheckSeverity.Fail),
            checks.Select(c => new ScheduleCheckDto(c.Code, c.Severity, c.Message)).ToList(), people, assignmentCount);
    }

    private static AssignmentDto ToDto(ScheduleAssignment a, IReadOnlyDictionary<Guid, string> names, IReadOnlyDictionary<Guid, string> templateNames) =>
        new(a.Id, a.Level, a.TargetId, names.GetValueOrDefault(a.TargetId, "—"), a.TemplateId,
            templateNames.GetValueOrDefault(a.TemplateId, "—"), a.EffectiveFrom, a.CreatedAt);
}
