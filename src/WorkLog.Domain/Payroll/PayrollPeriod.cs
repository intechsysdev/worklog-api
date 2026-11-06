using WorkLog.Domain.Common;
using WorkLog.Domain.Hours;

namespace WorkLog.Domain.Payroll;

public enum PayrollPeriodStatus
{
    Open = 1,
    Closed = 2
}

/// <summary>Quincena de nómina: del 1 al 15 y del 16 al fin de mes.</summary>
public readonly record struct Fortnight(DateOnly From, DateOnly To)
{
    public static Fortnight Containing(DateOnly date) =>
        date.Day <= 15
            ? new Fortnight(new DateOnly(date.Year, date.Month, 1), new DateOnly(date.Year, date.Month, 15))
            : new Fortnight(new DateOnly(date.Year, date.Month, 16), new DateOnly(date.Year, date.Month, DateTime.DaysInMonth(date.Year, date.Month)));

    public Fortnight Previous => Containing(From.AddDays(-1));

    public bool Contains(DateOnly date) => date >= From && date <= To;

    public string Label => From.Day == 1
        ? $"1–15 {MonthName(From.Month)} {From.Year}"
        : $"16–{To.Day} {MonthName(From.Month)} {From.Year}";

    private static string MonthName(int month) =>
        new[] { "ene", "feb", "mar", "abr", "may", "jun", "jul", "ago", "sep", "oct", "nov", "dic" }[month - 1];
}

/// <summary>Horas congeladas de una persona en un periodo cerrado.</summary>
public sealed class PayrollLine : Entity
{
    private PayrollLine()
    {
    }

    internal PayrollLine(Guid periodId, Guid memberId, string memberName, HoursBreakdown hours, string? absences)
    {
        PayrollPeriodId = periodId;
        MemberId = memberId;
        MemberName = memberName;
        OrdinaryDay = hours.OrdinaryDay;
        OrdinaryNight = hours.OrdinaryNight;
        OvertimeDay = hours.OvertimeDay;
        OvertimeNight = hours.OvertimeNight;
        SundayHoliday = hours.SundayHoliday;
        Absences = absences;
    }

    public Guid PayrollPeriodId { get; private set; }
    public Guid MemberId { get; private set; }
    public string MemberName { get; private set; } = string.Empty;
    public int OrdinaryDay { get; private set; }
    public int OrdinaryNight { get; private set; }
    public int OvertimeDay { get; private set; }
    public int OvertimeNight { get; private set; }
    public int SundayHoliday { get; private set; }
    public string? Absences { get; private set; }

    public HoursBreakdown Hours => new(OrdinaryDay, OrdinaryNight, OvertimeDay, OvertimeNight, SundayHoliday);
}

/// <summary>Línea calculada que se congela al cerrar.</summary>
public sealed record PayrollLineData(Guid MemberId, string MemberName, HoursBreakdown Hours, string? Absences);

/// <summary>
/// Cierre de una quincena para una sede. Mientras está abierta las horas son provisionales;
/// al cerrar se congelan y cualquier cambio posterior entra como ajuste en el periodo siguiente.
/// </summary>
public sealed class PayrollPeriod : TenantEntity
{
    private readonly List<PayrollLine> _lines = [];

    private PayrollPeriod()
    {
    }

    private PayrollPeriod(Guid tenantId) : base(tenantId)
    {
    }

    public Guid BranchId { get; private set; }
    public DateOnly From { get; private set; }
    public DateOnly To { get; private set; }
    public PayrollPeriodStatus Status { get; private set; }
    public Guid? ClosedBy { get; private set; }
    public DateTimeOffset? ClosedAt { get; private set; }
    public Guid? ReopenedBy { get; private set; }
    public DateTimeOffset? ReopenedAt { get; private set; }

    public IReadOnlyList<PayrollLine> Lines => _lines;

    public bool IsClosed => Status == PayrollPeriodStatus.Closed;

    public static PayrollPeriod Open(Guid tenantId, Guid branchId, Fortnight fortnight) => new(tenantId)
    {
        BranchId = branchId,
        From = fortnight.From,
        To = fortnight.To,
        Status = PayrollPeriodStatus.Open
    };

    /// <summary>Cierra con el checklist completo. Un periodo que no ha terminado no se cierra.</summary>
    public Result Close(Guid closedBy, DateTimeOffset at, DateOnly today, PayrollChecklist checklist, IReadOnlyList<PayrollLineData> lines)
    {
        if (IsClosed) return DomainError.Conflict("period.closed", "El periodo ya está cerrado.");
        if (today <= To) return DomainError.Validation("period.not_finished", $"El periodo termina el {To:dd/MM}: ciérralo después de esa fecha.");
        if (!checklist.IsComplete) return DomainError.Validation("period.checklist", "Completa la lista de revisión antes de cerrar.", "checklist");

        _lines.Clear();
        _lines.AddRange(lines.Select(l => new PayrollLine(Id, l.MemberId, l.MemberName, l.Hours, l.Absences)));
        Status = PayrollPeriodStatus.Closed;
        ClosedBy = closedBy;
        ClosedAt = at;
        return Result.Success();
    }

    public Result Reopen(Guid by, DateTimeOffset at)
    {
        if (!IsClosed) return DomainError.Conflict("period.open", "El periodo ya está abierto.");

        Status = PayrollPeriodStatus.Open;
        ReopenedBy = by;
        ReopenedAt = at;
        _lines.Clear();
        return Result.Success();
    }
}

/// <summary>Las tres revisiones obligatorias antes de cerrar.</summary>
public sealed record PayrollChecklist(bool ReviewedHourAlerts, bool ReviewedAbsences, bool ResolvedRequests)
{
    public bool IsComplete => ReviewedHourAlerts && ReviewedAbsences && ResolvedRequests;
}
