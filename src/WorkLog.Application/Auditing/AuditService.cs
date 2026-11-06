using System.Text;
using WorkLog.Application.Abstractions;
using WorkLog.Application.Common;
using WorkLog.Domain.Auditing;
using WorkLog.Domain.Geo;
using WorkLog.Domain.Workdays;

namespace WorkLog.Application.Auditing;

public sealed record AuditEventDto(
    Guid Id,
    DateTimeOffset OccurredAt,
    DateTimeOffset? ReceivedAt,
    string ActorName,
    string? ActorRole,
    AuditOrigin Origin,
    string Action,
    string Summary,
    string EntityType,
    string? EntityId,
    string? Before,
    string? After);

public sealed record LocationPointDto(
    DateTimeOffset CapturedAt,
    double Latitude,
    double Longitude,
    double AccuracyMeters,
    LocationEvent Event,
    GeofenceStatus Geofence,
    Guid? ActivityRecordId);

public sealed record MemberLocationsDto(Guid MemberId, string MemberName, DateOnly Date, IReadOnlyList<LocationPointDto> Points);

public interface IAuditService
{
    Task<PagedResult<AuditEventDto>> SearchAsync(AuditFilter filter, CancellationToken ct = default);
    Task<OperationResult<AuditEventDto>> GetAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<string>> EntityTypesAsync(CancellationToken ct = default);
    Task<FileDownload> ExportCsvAsync(AuditFilter filter, CancellationToken ct = default);

    /// <summary>Puntos de ubicación de una persona en un día. La consulta misma queda en auditoría.</summary>
    Task<OperationResult<MemberLocationsDto>> GetLocationsAsync(Guid memberId, DateOnly date, CancellationToken ct = default);
}

internal sealed class AuditService(
    IAuditQueries queries,
    IMemberRepository members,
    IWorkdayRepository workdays,
    ILocationPointRepository locations,
    IAuditTrail audit,
    IUnitOfWork unitOfWork) : IAuditService
{
    private const int MaxExportRows = 10_000;

    public async Task<PagedResult<AuditEventDto>> SearchAsync(AuditFilter filter, CancellationToken ct = default) =>
        (await queries.SearchAsync(filter, ct)).Map(e => ToDto(e, includeDiff: false));

    public async Task<OperationResult<AuditEventDto>> GetAsync(Guid id, CancellationToken ct = default) =>
        await queries.FindAsync(id, ct) is { } found
            ? ToDto(found, includeDiff: true)
            : OperationResult<AuditEventDto>.NotFound("El evento no existe.");

    public Task<IReadOnlyList<string>> EntityTypesAsync(CancellationToken ct = default) => queries.EntityTypesAsync(ct);

    public async Task<FileDownload> ExportCsvAsync(AuditFilter filter, CancellationToken ct = default)
    {
        var page = await queries.SearchAsync(filter with { Page = new PageRequest(1, MaxExportRows, filter.Page.Search) }, ct);
        var csv = new StringBuilder();
        csv.AppendLine("Fecha;Recibido;Quién;Rol;Origen;Acción;Qué pasó;Entidad;Id");

        foreach (var e in page.Items)
        {
            csv.AppendLine(string.Join(';',
                e.OccurredAt.ToString("yyyy-MM-dd HH:mm:ss zzz", System.Globalization.CultureInfo.InvariantCulture),
                e.ReceivedAt?.ToString("yyyy-MM-dd HH:mm:ss zzz", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                Cell(e.ActorName), Cell(e.ActorRole), e.Origin, e.Action, Cell(e.Summary), Cell(e.EntityType), Cell(e.EntityId)));
        }

        audit.Record("audit.exported", $"Exportó {page.Items.Count} eventos de auditoría.", "Auditoría", null);
        await unitOfWork.SaveChangesAsync(ct);
        return new FileDownload("auditoria-worklog.csv", "text/csv; charset=utf-8",
            [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(csv.ToString())]);
    }

    public async Task<OperationResult<MemberLocationsDto>> GetLocationsAsync(Guid memberId, DateOnly date, CancellationToken ct = default)
    {
        var member = await members.FindAsync(memberId, ct);
        if (member is null) return OperationResult<MemberLocationsDto>.NotFound("La persona no existe.");

        var workday = await workdays.FindByDateAsync(memberId, date, ct);
        var points = workday is null ? [] : await locations.ListByWorkdayAsync(workday.Id, ct);

        // Ver la ubicación de alguien es un acceso a datos personales: queda registrado siempre.
        audit.Record("location.viewed", $"Consultó la ubicación de {member.FullName} del {date:dd/MM} ({points.Count} puntos).",
            "Ubicación", member.Id.ToString());
        await unitOfWork.SaveChangesAsync(ct);

        return new MemberLocationsDto(member.Id, member.FullName, date, points
            .Select(p => new LocationPointDto(p.CapturedAt, p.Latitude, p.Longitude, p.AccuracyMeters, p.Event, p.Geofence, p.ActivityRecordId))
            .ToList());
    }

    private static string Cell(string? text) =>
        text is null ? string.Empty
        : text.Contains(';', StringComparison.Ordinal) || text.Contains('"', StringComparison.Ordinal) || text.Contains('\n', StringComparison.Ordinal)
            ? $"\"{text.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
            : text;

    private static AuditEventDto ToDto(AuditEvent e, bool includeDiff) => new(
        e.Id, e.OccurredAt, e.ReceivedAt, e.ActorName, e.ActorRole, e.Origin, e.Action, e.Summary, e.EntityType, e.EntityId,
        includeDiff ? e.Before : null, includeDiff ? e.After : null);
}
