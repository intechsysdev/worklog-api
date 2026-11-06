using System.Globalization;
using WorkLog.Application.Abstractions;
using WorkLog.Application.Common;
using WorkLog.Domain.Access;
using WorkLog.Domain.Activities;
using WorkLog.Domain.Common;

namespace WorkLog.Application.Activities;

public sealed record WorkOrderDto(
    Guid Id,
    string Number,
    Guid ActivityTypeId,
    string ActivityName,
    Guid? WorkSiteId,
    string? WorkSiteName,
    Guid? AssigneeId,
    string? AssigneeName,
    DateOnly ScheduledDate,
    TimeOnly? ScheduledTime,
    string? Notes,
    WorkOrderStatus Status);

public sealed record SaveWorkOrderCommand(
    string? Number,
    Guid ActivityTypeId,
    Guid? WorkSiteId,
    Guid? AssigneeId,
    DateOnly ScheduledDate,
    TimeOnly? ScheduledTime,
    string? Notes);

public sealed record ImportLineError(int Line, string Message);

public sealed record ImportReport(int Created, int Updated, IReadOnlyList<ImportLineError> Errors);

public interface IWorkOrderService
{
    Task<PagedResult<WorkOrderDto>> SearchAsync(WorkOrderFilter filter, CancellationToken ct = default);
    Task<OperationResult<WorkOrderDto>> CreateAsync(SaveWorkOrderCommand command, CancellationToken ct = default);
    Task<OperationResult<WorkOrderDto>> UpdateAsync(Guid id, SaveWorkOrderCommand command, CancellationToken ct = default);
    Task<OperationResult<WorkOrderDto>> CancelAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Importa OT desde CSV (separado por coma o punto y coma) con encabezado:
    /// numero, actividad, sitio, tecnico, fecha (aaaa-mm-dd), hora (hh:mm), notas.
    /// Una OT con número existente se actualiza. Las líneas con error no detienen el resto.
    /// </summary>
    Task<OperationResult<ImportReport>> ImportCsvAsync(TextReader csv, CancellationToken ct = default);
}

internal sealed class WorkOrderService(
    ICurrentActor actor,
    IWorkOrderRepository workOrders,
    IActivityTypeRepository types,
    IWorkSiteRepository sites,
    IMemberRepository members,
    IAuditTrail audit,
    IUnitOfWork unitOfWork) : IWorkOrderService
{
    private const int MaxImportLines = 2000;

    public async Task<PagedResult<WorkOrderDto>> SearchAsync(WorkOrderFilter filter, CancellationToken ct = default)
    {
        var page = await workOrders.SearchAsync(filter, ct);
        var lookups = await LookupsAsync(ct);
        return page.Map(o => ToDto(o, lookups));
    }

    public async Task<OperationResult<WorkOrderDto>> CreateAsync(SaveWorkOrderCommand command, CancellationToken ct = default)
    {
        var lookups = await LookupsAsync(ct);
        if (Validate(command, lookups) is { } invalid) return OperationResult<WorkOrderDto>.From(invalid);

        if (await workOrders.NumberExistsAsync(Normalize(command.Number), null, ct))
            return OperationResult<WorkOrderDto>.Invalid("Ya existe una OT con ese número.", "number", "work_order.number_taken");

        var created = WorkOrder.Create(actor.TenantId, ToData(command));
        if (created.IsFailure) return OperationResult<WorkOrderDto>.From(created.Error!);

        workOrders.Add(created.Value);
        audit.Record("work_order.created", $"Creó la {created.Value.Number}.", "Órdenes de trabajo", created.Value.Id.ToString());
        await unitOfWork.SaveChangesAsync(ct);
        return ToDto(created.Value, lookups);
    }

    public async Task<OperationResult<WorkOrderDto>> UpdateAsync(Guid id, SaveWorkOrderCommand command, CancellationToken ct = default)
    {
        var order = await workOrders.FindAsync(id, ct);
        if (order is null) return OperationResult<WorkOrderDto>.NotFound("La OT no existe.");

        var lookups = await LookupsAsync(ct);
        if (Validate(command, lookups) is { } invalid) return OperationResult<WorkOrderDto>.From(invalid);

        if (await workOrders.NumberExistsAsync(Normalize(command.Number), id, ct))
            return OperationResult<WorkOrderDto>.Invalid("Ya existe una OT con ese número.", "number", "work_order.number_taken");

        var updated = order.Update(ToData(command));
        if (updated.IsFailure) return OperationResult<WorkOrderDto>.From(updated.Error!);

        audit.Record("work_order.updated", $"Actualizó la {order.Number}.", "Órdenes de trabajo", order.Id.ToString());
        await unitOfWork.SaveChangesAsync(ct);
        return ToDto(order, lookups);
    }

    public async Task<OperationResult<WorkOrderDto>> CancelAsync(Guid id, CancellationToken ct = default)
    {
        var order = await workOrders.FindAsync(id, ct);
        if (order is null) return OperationResult<WorkOrderDto>.NotFound("La OT no existe.");

        var cancelled = order.Cancel();
        if (cancelled.IsFailure) return OperationResult<WorkOrderDto>.From(cancelled.Error!);

        audit.Record("work_order.cancelled", $"Canceló la {order.Number}.", "Órdenes de trabajo", order.Id.ToString());
        await unitOfWork.SaveChangesAsync(ct);
        return ToDto(order, await LookupsAsync(ct));
    }

    public async Task<OperationResult<ImportReport>> ImportCsvAsync(TextReader csv, CancellationToken ct = default)
    {
        var header = await csv.ReadLineAsync(ct);
        if (string.IsNullOrWhiteSpace(header))
            return OperationResult<ImportReport>.Invalid("El archivo está vacío.", "file");

        var separator = header.Count(c => c == ';') > header.Count(c => c == ',') ? ';' : ',';
        var columns = Split(header, separator).Select(c => Text.Key(c)).ToList();
        int Col(params string[] names) => columns.FindIndex(c => names.Contains(c));

        var iNumber = Col("numero", "number", "ot");
        var iActivity = Col("actividad", "activity");
        var iSite = Col("sitio", "site");
        var iAssignee = Col("tecnico", "usuario", "correo", "email", "assignee");
        var iDate = Col("fecha", "date");
        var iTime = Col("hora", "time");
        var iNotes = Col("notas", "nota", "notes");

        if (iNumber < 0 || iActivity < 0 || iDate < 0)
            return OperationResult<ImportReport>.Invalid("El encabezado debe tener al menos: numero, actividad y fecha.", "file");

        var activityByName = (await types.ListAsync(true, ct)).ToDictionary(t => t.NameKey);
        var siteByName = (await sites.ListAsync(false, ct)).ToDictionary(s => s.NameKey);
        var memberByEmail = (await members.ListAllAsync(ct)).Where(m => m.Status != MemberStatus.Inactive).ToDictionary(m => m.Email);

        var errors = new List<ImportLineError>();
        var createdInFile = new Dictionary<string, WorkOrder>();
        int created = 0, updated = 0, lineNumber = 1;

        while (await csv.ReadLineAsync(ct) is { } line)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (lineNumber > MaxImportLines + 1)
            {
                errors.Add(new ImportLineError(lineNumber, $"Se importan máximo {MaxImportLines} líneas por archivo."));
                break;
            }

            var cells = Split(line, separator);
            string? Cell(int index) => index >= 0 && index < cells.Count ? Text.CleanOrNull(cells[index]) : null;

            if (!activityByName.TryGetValue(Text.Key(Cell(iActivity)), out var activity))
            {
                errors.Add(new ImportLineError(lineNumber, $"La actividad «{Cell(iActivity)}» no está en el catálogo."));
                continue;
            }

            Guid? siteId = null;
            if (Cell(iSite) is { } siteName)
            {
                if (!siteByName.TryGetValue(Text.Key(siteName), out var site))
                {
                    errors.Add(new ImportLineError(lineNumber, $"El sitio «{siteName}» no existe."));
                    continue;
                }

                siteId = site.Id;
            }

            Guid? assigneeId = null;
            if (Cell(iAssignee) is { } email)
            {
                if (!memberByEmail.TryGetValue(Member.NormalizeEmail(email), out var assignee))
                {
                    errors.Add(new ImportLineError(lineNumber, $"No hay un usuario con el correo «{email}»."));
                    continue;
                }

                assigneeId = assignee.Id;
            }

            if (!DateOnly.TryParseExact(Cell(iDate), ["yyyy-MM-dd", "dd/MM/yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                errors.Add(new ImportLineError(lineNumber, "La fecha debe tener el formato aaaa-mm-dd."));
                continue;
            }

            TimeOnly? time = null;
            if (Cell(iTime) is { } timeText)
            {
                if (!TimeOnly.TryParseExact(timeText, ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                {
                    errors.Add(new ImportLineError(lineNumber, "La hora debe tener el formato hh:mm."));
                    continue;
                }

                time = parsed;
            }

            var data = new WorkOrderData(Cell(iNumber) ?? string.Empty, activity.Id, siteId, assigneeId, date, time, Cell(iNotes));
            var number = Normalize(data.Number);
            var existing = createdInFile.GetValueOrDefault(number) ?? await workOrders.FindByNumberAsync(number, ct);

            if (existing is not null)
            {
                var result = existing.Update(data);
                if (result.IsFailure) errors.Add(new ImportLineError(lineNumber, result.Error!.Message));
                else updated++;
                continue;
            }

            var order = WorkOrder.Create(actor.TenantId, data);
            if (order.IsFailure)
            {
                errors.Add(new ImportLineError(lineNumber, order.Error!.Message));
                continue;
            }

            workOrders.Add(order.Value);
            createdInFile[number] = order.Value;
            created++;
        }

        audit.Record("work_order.imported", $"Importó OT desde CSV: {created} nuevas, {updated} actualizadas, {errors.Count} con error.",
            "Órdenes de trabajo", null);
        await unitOfWork.SaveChangesAsync(ct);
        return new ImportReport(created, updated, errors);
    }

    private sealed record Lookups(
        IReadOnlyDictionary<Guid, ActivityType> Activities,
        IReadOnlyDictionary<Guid, string> Sites,
        IReadOnlyDictionary<Guid, Member> People);

    private async Task<Lookups> LookupsAsync(CancellationToken ct) => new(
        (await types.ListAsync(false, ct)).ToDictionary(t => t.Id),
        (await sites.ListAsync(true, ct)).ToDictionary(s => s.Id, s => s.Name),
        (await members.ListAllAsync(ct)).ToDictionary(m => m.Id));

    private static DomainError? Validate(SaveWorkOrderCommand command, Lookups lookups)
    {
        if (!lookups.Activities.TryGetValue(command.ActivityTypeId, out var activity) || !activity.IsActive)
            return DomainError.Validation("work_order.activity", "Elige una actividad activa del catálogo.", "activityTypeId");

        if (command.WorkSiteId is { } site && !lookups.Sites.ContainsKey(site))
            return DomainError.Validation("work_order.site", "Elige un sitio de la lista.", "workSiteId");

        if (command.AssigneeId is { } assignee && (!lookups.People.TryGetValue(assignee, out var person) || person.Status == MemberStatus.Inactive))
            return DomainError.Validation("work_order.assignee", "Elige una persona activa.", "assigneeId");

        return null;
    }

    private static string Normalize(string? number) => Text.Clean(number).ToUpperInvariant();

    private static WorkOrderData ToData(SaveWorkOrderCommand c) =>
        new(c.Number ?? string.Empty, c.ActivityTypeId, c.WorkSiteId, c.AssigneeId, c.ScheduledDate, c.ScheduledTime, c.Notes);

    private static WorkOrderDto ToDto(WorkOrder o, Lookups l) => new(
        o.Id,
        o.Number,
        o.ActivityTypeId,
        l.Activities.TryGetValue(o.ActivityTypeId, out var a) ? a.Name : "—",
        o.WorkSiteId,
        o.WorkSiteId is { } s ? l.Sites.GetValueOrDefault(s) : null,
        o.AssigneeId,
        o.AssigneeId is { } p && l.People.TryGetValue(p, out var person) ? person.FullName : null,
        o.ScheduledDate,
        o.ScheduledTime,
        o.Notes,
        o.Status);

    /// <summary>Divide una línea CSV respetando comillas dobles.</summary>
    private static List<string> Split(string line, char separator)
    {
        var cells = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else if (c == separator && !quoted)
            {
                cells.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        cells.Add(current.ToString());
        return cells;
    }
}
