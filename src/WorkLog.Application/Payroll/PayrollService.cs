using System.Globalization;
using System.Text;
using WorkLog.Application.Abstractions;
using WorkLog.Application.Common;
using WorkLog.Application.Hours;
using WorkLog.Domain.Absences;
using WorkLog.Domain.Access;
using WorkLog.Domain.Payroll;
using WorkLog.Domain.Requests;
using WorkLog.Domain.Scheduling;

namespace WorkLog.Application.Payroll;

public enum PayrollMemberStatus
{
    Pending = 1,
    Ready = 2,
    Closed = 3
}

public sealed record PeriodOptionDto(DateOnly From, DateOnly To, string Label, PayrollPeriodStatus Status);

public sealed record PayrollRowDto(Guid MemberId, string Name, BreakdownDto Hours, string? Absences, PayrollMemberStatus Status);

public sealed record PendingRequestDto(Guid RequestId, string MemberName, RequestType Type);

public sealed record PayrollSummaryDto(
    Guid BranchId,
    string BranchName,
    DateOnly From,
    DateOnly To,
    string Label,
    PayrollPeriodStatus Status,
    bool CanClose,
    string? ClosedByName,
    DateTimeOffset? ClosedAt,
    IReadOnlyList<PayrollRowDto> Rows,
    BreakdownDto Totals,
    IReadOnlyList<PendingRequestDto> PendingRequests);

public sealed record ClosePeriodCommand(bool ReviewedHourAlerts, bool ReviewedAbsences, bool ResolvedRequests);

public enum ExportFormat
{
    Csv = 1,
    Xlsx = 2
}

public interface IPayrollService
{
    Task<OperationResult<IReadOnlyList<PeriodOptionDto>>> ListPeriodsAsync(Guid branchId, CancellationToken ct = default);
    Task<OperationResult<PayrollSummaryDto>> GetSummaryAsync(Guid branchId, DateOnly from, CancellationToken ct = default);
    Task<OperationResult<PayrollSummaryDto>> CloseAsync(Guid branchId, DateOnly from, ClosePeriodCommand command, CancellationToken ct = default);
    Task<OperationResult<PayrollSummaryDto>> ReopenAsync(Guid branchId, DateOnly from, CancellationToken ct = default);
    Task<OperationResult<FileDownload>> ExportAsync(Guid branchId, DateOnly from, ExportFormat format, CancellationToken ct = default);
}

internal sealed class PayrollService(
    ICurrentActor actor,
    IPayrollPeriodRepository periods,
    IBranchRepository branches,
    IMemberRepository members,
    IWorkRequestRepository requests,
    IAbsenceQueries absences,
    IHoursEngine engine,
    ISpreadsheetWriter spreadsheet,
    IAuditTrail audit,
    IUnitOfWork unitOfWork,
    TimeProvider clock) : IPayrollService
{
    private const int PeriodsListed = 8;

    public async Task<OperationResult<IReadOnlyList<PeriodOptionDto>>> ListPeriodsAsync(Guid branchId, CancellationToken ct = default)
    {
        var branch = await branches.FindAsync(branchId, ct);
        if (branch is null) return OperationResult<IReadOnlyList<PeriodOptionDto>>.NotFound("La sede no existe.");

        var stored = (await periods.ListAsync(branchId, ct)).ToDictionary(p => p.From);
        var fortnight = Fortnight.Containing(LocalTime.Today(clock, LocalTime.Find(branch.TimeZoneId)));
        var options = new List<PeriodOptionDto>();

        for (var i = 0; i < PeriodsListed; i++, fortnight = fortnight.Previous)
        {
            var status = stored.TryGetValue(fortnight.From, out var p) ? p.Status : PayrollPeriodStatus.Open;
            options.Add(new PeriodOptionDto(fortnight.From, fortnight.To, fortnight.Label, status));
        }

        return options;
    }

    public async Task<OperationResult<PayrollSummaryDto>> GetSummaryAsync(Guid branchId, DateOnly from, CancellationToken ct = default)
    {
        var branch = await branches.FindAsync(branchId, ct);
        if (branch is null) return OperationResult<PayrollSummaryDto>.NotFound("La sede no existe.");

        var fortnight = Fortnight.Containing(from);
        var period = await periods.FindAsync(branchId, fortnight.From, ct);
        var today = LocalTime.Today(clock, LocalTime.Find(branch.TimeZoneId));

        IReadOnlyList<PayrollRowDto> rows;
        IReadOnlyList<PendingRequestDto> pending = [];

        if (period is { IsClosed: true })
        {
            rows = period.Lines
                .OrderBy(l => l.MemberName)
                .Select(l => new PayrollRowDto(l.MemberId, l.MemberName, BreakdownDto.From(l.Hours), l.Absences, PayrollMemberStatus.Closed))
                .ToList();
        }
        else
        {
            var lines = await ComputeLinesAsync(branchId, fortnight, ct);
            var people = lines.Select(l => l.MemberId).ToList();
            var pendingRequests = await requests.ListAsync(people, fortnight.From, fortnight.To, RequestStatus.Pending, ct);
            var names = lines.ToDictionary(l => l.MemberId, l => l.MemberName);

            pending = pendingRequests.Select(r => new PendingRequestDto(r.Id, names.GetValueOrDefault(r.MemberId, string.Empty), r.Type)).ToList();
            rows = lines
                .OrderBy(l => l.MemberName)
                .Select(l => new PayrollRowDto(l.MemberId, l.MemberName, BreakdownDto.From(l.Hours), l.Absences,
                    pendingRequests.Any(r => r.MemberId == l.MemberId) ? PayrollMemberStatus.Pending : PayrollMemberStatus.Ready))
                .ToList();
        }

        var totals = rows.Aggregate(new BreakdownDto(0, 0, 0, 0, 0, 0), (acc, r) => new BreakdownDto(
            acc.OrdinaryDay + r.Hours.OrdinaryDay, acc.OrdinaryNight + r.Hours.OrdinaryNight, acc.OvertimeDay + r.Hours.OvertimeDay,
            acc.OvertimeNight + r.Hours.OvertimeNight, acc.SundayHoliday + r.Hours.SundayHoliday, acc.Total + r.Hours.Total));

        var closedBy = period?.ClosedBy is { } by ? (await members.FindAsync(by, ct))?.FullName : null;

        return new PayrollSummaryDto(branch.Id, branch.Name, fortnight.From, fortnight.To, fortnight.Label,
            period?.Status ?? PayrollPeriodStatus.Open, period is not { IsClosed: true } && today > fortnight.To,
            closedBy, period?.ClosedAt, rows, totals, pending);
    }

    public async Task<OperationResult<PayrollSummaryDto>> CloseAsync(Guid branchId, DateOnly from, ClosePeriodCommand command, CancellationToken ct = default)
    {
        var branch = await branches.FindAsync(branchId, ct);
        if (branch is null) return OperationResult<PayrollSummaryDto>.NotFound("La sede no existe.");

        var fortnight = Fortnight.Containing(from);
        var period = await periods.FindAsync(branchId, fortnight.From, ct);
        if (period is null)
        {
            period = PayrollPeriod.Open(actor.TenantId, branchId, fortnight);
            periods.Add(period);
        }

        var lines = await ComputeLinesAsync(branchId, fortnight, ct);
        var today = LocalTime.Today(clock, LocalTime.Find(branch.TimeZoneId));
        var closed = period.Close(actor.MemberId, clock.GetUtcNow(), today,
            new PayrollChecklist(command.ReviewedHourAlerts, command.ReviewedAbsences, command.ResolvedRequests), lines);
        if (closed.IsFailure) return OperationResult<PayrollSummaryDto>.From(closed.Error!);

        audit.Record("period.closed", $"Cerró el periodo {fortnight.Label} de {branch.Name} ({lines.Count} personas).", "Periodos",
            $"{branchId}:{fortnight.From:yyyy-MM-dd}");
        await unitOfWork.SaveChangesAsync(ct);
        return await GetSummaryAsync(branchId, fortnight.From, ct);
    }

    public async Task<OperationResult<PayrollSummaryDto>> ReopenAsync(Guid branchId, DateOnly from, CancellationToken ct = default)
    {
        if (actor.RoleKey != SystemRoles.Administrator)
            return OperationResult<PayrollSummaryDto>.Forbidden("Solo el administrador puede reabrir un periodo.");

        var fortnight = Fortnight.Containing(from);
        var period = await periods.FindAsync(branchId, fortnight.From, ct);
        if (period is null) return OperationResult<PayrollSummaryDto>.Conflict("El periodo no está cerrado.");

        var reopened = period.Reopen(actor.MemberId, clock.GetUtcNow());
        if (reopened.IsFailure) return OperationResult<PayrollSummaryDto>.From(reopened.Error!);

        audit.Record("period.reopened", $"Reabrió el periodo {fortnight.Label}.", "Periodos", $"{branchId}:{fortnight.From:yyyy-MM-dd}");
        await unitOfWork.SaveChangesAsync(ct);
        return await GetSummaryAsync(branchId, fortnight.From, ct);
    }

    public async Task<OperationResult<FileDownload>> ExportAsync(Guid branchId, DateOnly from, ExportFormat format, CancellationToken ct = default)
    {
        var summary = await GetSummaryAsync(branchId, from, ct);
        if (!summary.Succeeded) return OperationResult<FileDownload>.From(summary);

        var s = summary.Value!;
        if (s.Status != PayrollPeriodStatus.Closed)
            return OperationResult<FileDownload>.Conflict("Cierra el periodo antes de exportarlo para nómina: abierto, las cifras son provisionales.", "period.open");

        string[] headers = ["Usuario", "Ordinarias diurnas (h)", "Ordinarias nocturnas (h)", "Extra diurnas (h)", "Extra nocturnas (h)", "Dominical y festivo (h)", "Total (h)", "Novedades"];
        var rows = s.Rows.Select(r => (IReadOnlyList<object?>)
        [
            r.Name, Hours(r.Hours.OrdinaryDay), Hours(r.Hours.OrdinaryNight), Hours(r.Hours.OvertimeDay),
            Hours(r.Hours.OvertimeNight), Hours(r.Hours.SundayHoliday), Hours(r.Hours.Total), r.Absences
        ]).ToList();

        var baseName = $"nomina-{Slug(s.BranchName)}-{s.From:yyyyMMdd}-{s.To:yyyyMMdd}";
        audit.Record("period.exported", $"Exportó el periodo {s.Label} de {s.BranchName} en {format.ToString().ToUpperInvariant()}.", "Periodos",
            $"{branchId}:{s.From:yyyy-MM-dd}");
        await unitOfWork.SaveChangesAsync(ct);

        if (format == ExportFormat.Xlsx)
        {
            return new FileDownload($"{baseName}.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                spreadsheet.Write(s.Label, headers, rows));
        }

        // CSV con BOM y punto y coma: Excel en español lo abre sin asistente de importación.
        var csv = new StringBuilder();
        csv.AppendLine(string.Join(';', headers));
        foreach (var row in rows)
            csv.AppendLine(string.Join(';', row.Select(cell => Csv(cell))));
        return new FileDownload($"{baseName}.csv", "text/csv; charset=utf-8", [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(csv.ToString())]);
    }

    private async Task<IReadOnlyList<PayrollLineData>> ComputeLinesAsync(Guid branchId, Fortnight fortnight, CancellationToken ct)
    {
        var people = (await members.ListMobileAsync(branchId, ct)).Where(m => m.Status != MemberStatus.Invited).ToList();
        var hours = await engine.ComputeAsync(people, fortnight.From, fortnight.To, ct);
        var absenceList = await absences.ListAsync(people.Select(p => p.Id).ToList(), fortnight.From, fortnight.To, ct);

        return hours.Select(h =>
        {
            var mine = absenceList.Where(a => a.MemberId == h.Member.Id && a.Status == AbsenceStatus.Approved).ToList();
            var text = mine.Count == 0
                ? null
                : string.Join("; ", mine.Select(a => $"{ShiftResolver.AbsenceLabel(a.Type)} {Max(a.From, fortnight.From):dd/MM}–{Min(a.To, fortnight.To):dd/MM}"));
            return new PayrollLineData(h.Member.Id, h.Member.FullName, h.Total, text);
        }).ToList();
    }

    private static DateOnly Max(DateOnly a, DateOnly b) => a > b ? a : b;
    private static DateOnly Min(DateOnly a, DateOnly b) => a < b ? a : b;

    private static decimal Hours(int minutes) => Math.Round(minutes / 60m, 2);

    private static string Csv(object? cell) => cell switch
    {
        null => string.Empty,
        decimal d => d.ToString("0.##", CultureInfo.GetCultureInfo("es-CO")),
        string text when text.Contains(';', StringComparison.Ordinal) || text.Contains('"', StringComparison.Ordinal) =>
            $"\"{text.Replace("\"", "\"\"", StringComparison.Ordinal)}\"",
        _ => cell.ToString() ?? string.Empty
    };

    private static string Slug(string text) =>
        string.Concat(Domain.Common.Text.Key(text).Select(c => char.IsLetterOrDigit(c) ? c : '-')).Trim('-');
}
