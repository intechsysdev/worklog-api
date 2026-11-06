using WorkLog.Domain.Common;

namespace WorkLog.Domain.Activities;

public enum WorkOrderStatus
{
    Open = 1,
    InProgress = 2,
    Completed = 3,
    Incomplete = 4,
    Rescheduled = 5,
    Cancelled = 6
}

/// <summary>
/// Orden de trabajo (OT): una actividad programada para una persona en un sitio y una fecha.
/// Aparece en "Asignadas hoy" de la app y su estado sigue al resultado con que se finaliza.
/// </summary>
public sealed class WorkOrder : TenantEntity
{
    private WorkOrder()
    {
    }

    private WorkOrder(Guid tenantId) : base(tenantId)
    {
    }

    public string Number { get; private set; } = string.Empty;
    public Guid ActivityTypeId { get; private set; }
    public Guid? WorkSiteId { get; private set; }
    public Guid? AssigneeId { get; private set; }
    public DateOnly ScheduledDate { get; private set; }
    public TimeOnly? ScheduledTime { get; private set; }
    public string? Notes { get; private set; }
    public WorkOrderStatus Status { get; private set; } = WorkOrderStatus.Open;

    /// <summary>Se puede trabajar: no está cerrada ni cancelada.</summary>
    public bool IsWorkable => Status is WorkOrderStatus.Open or WorkOrderStatus.InProgress or WorkOrderStatus.Incomplete;

    public static Result<WorkOrder> Create(Guid tenantId, WorkOrderData data)
    {
        var order = new WorkOrder(tenantId);
        var applied = order.Update(data);
        return applied.IsSuccess ? order : applied.Error!;
    }

    public Result Update(WorkOrderData data)
    {
        var number = Text.Clean(data.Number).ToUpperInvariant();
        if (Guard.Name(number, "number", 2, 40, "El número de la OT") is { } numberError) return numberError;

        if (data.ActivityTypeId == Guid.Empty)
            return DomainError.Validation("work_order.activity", "Elige la actividad.", "activityTypeId");

        if (data.ScheduledDate == default)
            return DomainError.Validation("work_order.date", "Elige la fecha programada.", "scheduledDate");

        var notes = Text.CleanOrNull(data.Notes);
        if (Guard.MaxLength(notes, "notes", 500, "La nota") is { } notesError) return notesError;

        Number = number;
        ActivityTypeId = data.ActivityTypeId;
        WorkSiteId = data.WorkSiteId;
        AssigneeId = data.AssigneeId;
        ScheduledDate = data.ScheduledDate;
        ScheduledTime = data.ScheduledTime;
        Notes = notes;
        return Result.Success();
    }

    public void MarkStarted()
    {
        if (Status is WorkOrderStatus.Open or WorkOrderStatus.Incomplete) Status = WorkOrderStatus.InProgress;
    }

    /// <summary>La OT toma el resultado con que se finalizó la actividad.</summary>
    public void ApplyOutcome(ActivityOutcome outcome) => Status = outcome switch
    {
        ActivityOutcome.Completed => WorkOrderStatus.Completed,
        ActivityOutcome.Incomplete => WorkOrderStatus.Incomplete,
        _ => WorkOrderStatus.Rescheduled
    };

    public Result Cancel()
    {
        if (Status is WorkOrderStatus.Completed)
            return DomainError.Conflict("work_order.completed", "Una OT completada no se cancela.");

        Status = WorkOrderStatus.Cancelled;
        return Result.Success();
    }

    public Result Reschedule(DateOnly date, TimeOnly? time)
    {
        if (Status is WorkOrderStatus.Completed or WorkOrderStatus.Cancelled)
            return DomainError.Conflict("work_order.closed", "La OT ya está cerrada.");

        ScheduledDate = date;
        ScheduledTime = time;
        Status = WorkOrderStatus.Open;
        return Result.Success();
    }
}

public sealed record WorkOrderData(
    string Number,
    Guid ActivityTypeId,
    Guid? WorkSiteId,
    Guid? AssigneeId,
    DateOnly ScheduledDate,
    TimeOnly? ScheduledTime,
    string? Notes);
