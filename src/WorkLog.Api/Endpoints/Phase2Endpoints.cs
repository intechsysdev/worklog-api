using WorkLog.Api.Extensions;
using WorkLog.Application.Absences;
using WorkLog.Application.Abstractions;
using WorkLog.Application.Auditing;
using WorkLog.Application.Common;
using WorkLog.Application.Devices;
using WorkLog.Application.Hours;
using WorkLog.Application.Payroll;
using WorkLog.Application.Requests;
using WorkLog.Domain.Absences;
using WorkLog.Domain.Access;
using WorkLog.Domain.Auditing;
using WorkLog.Domain.Requests;

namespace WorkLog.Api.Endpoints;

/// <summary>Solicitudes, novedades, horas, cierre de periodo, auditoría y control de dispositivos.</summary>
public static class Phase2Endpoints
{
    public static IEndpointRouteBuilder MapPhase2Endpoints(this IEndpointRouteBuilder app)
    {
        MapMine(app);
        MapApprovals(app);
        MapAbsences(app);
        MapHours(app);
        MapPayroll(app);
        MapAudit(app);
        MapDevices(app);
        return app;
    }

    /// <summary>Lo que el usuario móvil hace con sus solicitudes y su semana.</summary>
    private static void MapMine(IEndpointRouteBuilder app)
    {
        var me = app.MapGroup("/api/v1/me").WithTags("Mis solicitudes").RequireAuthorization().RequirePermission(Permission.ViewOwn);

        me.MapGet("/week", async (DateOnly? date, IHoursService hours, CancellationToken ct) => (await hours.GetMyWeekAsync(date, ct)).ToHttpResult())
            .WithTags("Mi jornada").Produces<MyWeekDto>()
            .WithSummary("Mi semana: horas por día frente al tope legal, programadas y desglose (provisional hasta el cierre).");

        me.MapGet("/requests", async (RequestStatus? status, int? page, int? pageSize, IRequestService requests, CancellationToken ct) =>
                (await requests.ListMineAsync(status, new PageRequest(page ?? 1, pageSize ?? 30), ct)).ToHttpResult())
            .Produces<PagedResult<RequestDto>>().WithSummary("Mis solicitudes (todas, pendientes o resueltas).");

        me.MapPost("/requests/adjustment", async (SubmitAdjustmentCommand command, IRequestService requests, CancellationToken ct) =>
                (await requests.SubmitAdjustmentAsync(command, ct)).ToCreatedResult(r => $"/api/v1/me/requests/{r.Id}"))
            .Produces<RequestDto>(201).WithSummary("Pide corregir una hora mal registrada.");

        me.MapPost("/requests/overtime", async (SubmitOvertimeCommand command, IRequestService requests, CancellationToken ct) =>
                (await requests.SubmitOvertimeAsync(command, ct)).ToCreatedResult(r => $"/api/v1/me/requests/{r.Id}"))
            .Produces<RequestDto>(201).WithSummary("Pide autorización de horas extra.");

        me.MapPost("/requests/shift-change", async (SubmitShiftChangeCommand command, IRequestService requests, CancellationToken ct) =>
                (await requests.SubmitShiftChangeAsync(command, ct)).ToCreatedResult(r => $"/api/v1/me/requests/{r.Id}"))
            .Produces<RequestDto>(201).WithSummary("Pide cambiar el turno de uno o varios días.");

        me.MapPost("/requests/{id:guid}/cancel", async (Guid id, IRequestService requests, CancellationToken ct) =>
                (await requests.CancelAsync(id, ct)).ToHttpResult())
            .Produces<RequestDto>().WithSummary("Cancela una solicitud pendiente propia.");
    }

    private static void MapApprovals(IEndpointRouteBuilder app)
    {
        var approvals = app.MapGroup("/api/v1/approvals").WithTags("Aprobaciones").RequireAuthorization().RequirePermission(Permission.ApproveRequests);

        approvals.MapGet("/", async (RequestType? type, RequestStatus? status, Guid? branchId, int? page, int? pageSize, IRequestService requests, CancellationToken ct) =>
                (await requests.SearchAsync(type, status, branchId, new PageRequest(page ?? 1, pageSize ?? 30), ct)).ToHttpResult())
            .Produces<PagedResult<RequestDto>>().WithSummary("Solicitudes del equipo (pendientes primero).");

        approvals.MapGet("/pending-count", async (IRequestService requests, CancellationToken ct) => Results.Ok(new { count = await requests.CountPendingAsync(ct) }))
            .WithSummary("Cantidad de solicitudes pendientes (badge del menú).");

        approvals.MapGet("/{id:guid}", async (Guid id, IRequestService requests, CancellationToken ct) => (await requests.GetAsync(id, ct)).ToHttpResult())
            .Produces<RequestDetailDto>().WithSummary("Detalle con el efecto en las horas.");

        approvals.MapPost("/{id:guid}/approve", async (Guid id, ResolveRequestCommand command, IRequestService requests, CancellationToken ct) =>
                (await requests.ApproveAsync(id, command, ct)).ToHttpResult())
            .Produces<RequestDetailDto>().WithSummary("Aprueba y aplica el cambio (ajuste, horas extra o cambio de turno).");

        approvals.MapPost("/{id:guid}/reject", async (Guid id, ResolveRequestCommand command, IRequestService requests, CancellationToken ct) =>
                (await requests.RejectAsync(id, command, ct)).ToHttpResult())
            .Produces<RequestDetailDto>().WithSummary("Rechaza con comentario obligatorio para el usuario.");
    }

    private static void MapAbsences(IEndpointRouteBuilder app)
    {
        var absences = app.MapGroup("/api/v1/absences").WithTags("Novedades").RequireAuthorization().RequirePermission(Permission.ManageAbsences);

        absences.MapGet("/", async (AbsenceType? type, AbsenceStatus? status, Guid? memberId, DateOnly? from, DateOnly? to, int? page, int? pageSize,
                IAbsenceService service, CancellationToken ct) =>
                (await service.SearchAsync(new AbsenceFilter(new PageRequest(page ?? 1, pageSize ?? 30), type, status, memberId, from, to), ct)).ToHttpResult())
            .Produces<PagedResult<AbsenceDto>>().WithSummary("Novedades por tipo, estado, persona y fechas.");

        absences.MapPost("/preview", async (RegisterAbsenceCommand command, IAbsenceService service, CancellationToken ct) =>
                (await service.PreviewAsync(command, ct)).ToHttpResult())
            .Produces<AbsencePreviewDto>().WithSummary("Calcula los días hábiles que descuenta, sin guardar.");

        absences.MapPost("/", async (RegisterAbsenceCommand command, IAbsenceService service, CancellationToken ct) =>
                (await service.RegisterAsync(command, ct)).ToCreatedResult(a => $"/api/v1/absences/{a.Id}"))
            .Produces<AbsenceDto>(201).WithSummary("Registra una novedad (queda pendiente).");

        absences.MapPost("/{id:guid}/support", async (Guid id, IFormFile file, IAbsenceService service, CancellationToken ct) =>
            {
                await using var stream = file.OpenReadStream();
                return (await service.AttachSupportAsync(id, new UploadSupportCommand(stream, file.ContentType, file.Length, file.FileName), ct)).ToHttpResult();
            })
            .DisableAntiforgery().Produces<AbsenceDto>().WithSummary("Adjunta el soporte (PDF o foto, máx. 10 MB).");

        absences.MapGet("/{id:guid}/support", async (Guid id, IAbsenceService service, CancellationToken ct) =>
            {
                var file = await service.OpenSupportAsync(id, ct);
                return file.Succeeded ? Results.File(file.Value!.Content, file.Value.ContentType) : file.ToHttpResult();
            })
            .WithSummary("Descarga el soporte.");

        absences.MapPost("/{id:guid}/approve", async (Guid id, IAbsenceService service, CancellationToken ct) => (await service.ApproveAsync(id, ct)).ToHttpResult())
            .Produces<AbsenceDto>().WithSummary("Aprueba la novedad: esos días la persona no tiene turno.");

        absences.MapPost("/{id:guid}/reject", async (Guid id, IAbsenceService service, CancellationToken ct) => (await service.RejectAsync(id, ct)).ToHttpResult())
            .Produces<AbsenceDto>().WithSummary("Rechaza la novedad.");
    }

    private static void MapHours(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/hours/team", async (DateOnly? date, Guid? branchId, bool? onlyAlerts, IHoursService hours, CancellationToken ct) =>
                (await hours.GetTeamWeekAsync(date, branchId, onlyAlerts ?? false, ct)).ToHttpResult())
            .WithTags("Horas").RequireAuthorization().RequirePermission(Permission.ViewTeam)
            .Produces<TeamHoursDto>().WithSummary("Horas del equipo en la semana frente al tope legal, con alertas.");
    }

    private static void MapPayroll(IEndpointRouteBuilder app)
    {
        var payroll = app.MapGroup("/api/v1/payroll/{branchId:guid}").WithTags("Cierre de periodo").RequireAuthorization();

        payroll.MapGet("/periods", async (Guid branchId, IPayrollService service, CancellationToken ct) => (await service.ListPeriodsAsync(branchId, ct)).ToHttpResult())
            .RequireAnyPermission(Permission.ClosePeriods, Permission.ExportPayroll)
            .Produces<IReadOnlyList<PeriodOptionDto>>().WithSummary("Quincenas recientes de la sede y su estado.");

        payroll.MapGet("/periods/{from}", async (Guid branchId, DateOnly from, IPayrollService service, CancellationToken ct) =>
                (await service.GetSummaryAsync(branchId, from, ct)).ToHttpResult())
            .RequireAnyPermission(Permission.ClosePeriods, Permission.ExportPayroll)
            .Produces<PayrollSummaryDto>().WithSummary("Horas por persona del periodo, pendientes y estado.");

        payroll.MapPost("/periods/{from}/close", async (Guid branchId, DateOnly from, ClosePeriodCommand command, IPayrollService service, CancellationToken ct) =>
                (await service.CloseAsync(branchId, from, command, ct)).ToHttpResult())
            .RequirePermission(Permission.ClosePeriods)
            .Produces<PayrollSummaryDto>().WithSummary("Cierra el periodo con el checklist: las horas quedan congeladas.");

        payroll.MapPost("/periods/{from}/reopen", async (Guid branchId, DateOnly from, IPayrollService service, CancellationToken ct) =>
                (await service.ReopenAsync(branchId, from, ct)).ToHttpResult())
            .RequirePermission(Permission.ClosePeriods)
            .Produces<PayrollSummaryDto>().WithSummary("Reabre un periodo cerrado (solo administrador).");

        payroll.MapGet("/periods/{from}/export", async (Guid branchId, DateOnly from, ExportFormat? format, IPayrollService service, CancellationToken ct) =>
            {
                var file = await service.ExportAsync(branchId, from, format ?? ExportFormat.Xlsx, ct);
                return file.Succeeded ? Results.File(file.Value!.Content, file.Value.ContentType, file.Value.FileName) : file.ToHttpResult();
            })
            .RequirePermission(Permission.ExportPayroll).WithSummary("Exporta el periodo cerrado para nómina (xlsx o csv).");
    }

    private static void MapAudit(IEndpointRouteBuilder app)
    {
        var auditGroup = app.MapGroup("/api/v1/audit").WithTags("Auditoría").RequireAuthorization().RequirePermission(Permission.ViewAudit);

        auditGroup.MapGet("/", async (AuditOrigin? origin, string? entityType, DateTimeOffset? from, DateTimeOffset? to, string? search,
                int? page, int? pageSize, IAuditService service, CancellationToken ct) =>
                Results.Ok(await service.SearchAsync(new AuditFilter(new PageRequest(page ?? 1, pageSize ?? 50, search), origin, entityType, from, to), ct)))
            .Produces<PagedResult<AuditEventDto>>().WithSummary("Registro de auditoría (solo lectura).");

        auditGroup.MapGet("/entity-types", async (IAuditService service, CancellationToken ct) => Results.Ok(await service.EntityTypesAsync(ct)))
            .Produces<IReadOnlyList<string>>().WithSummary("Entidades presentes en la auditoría (para el filtro).");

        auditGroup.MapGet("/{id:guid}", async (Guid id, IAuditService service, CancellationToken ct) => (await service.GetAsync(id, ct)).ToHttpResult())
            .Produces<AuditEventDto>().WithSummary("Detalle con antes y después.");

        auditGroup.MapGet("/export", async (AuditOrigin? origin, string? entityType, DateTimeOffset? from, DateTimeOffset? to, string? search,
                IAuditService service, CancellationToken ct) =>
            {
                var file = await service.ExportCsvAsync(new AuditFilter(new PageRequest(1, 1, search), origin, entityType, from, to), ct);
                return Results.File(file.Content, file.ContentType, file.FileName);
            })
            .WithSummary("Exporta la auditoría filtrada a CSV (máx. 10.000 eventos).");

        app.MapGet("/api/v1/members/{id:guid}/locations", async (Guid id, DateOnly date, IAuditService service, CancellationToken ct) =>
                (await service.GetLocationsAsync(id, date, ct)).ToHttpResult())
            .WithTags("Equipo").RequireAuthorization().RequirePermission(Permission.ViewLocations)
            .Produces<MemberLocationsDto>().WithSummary("Puntos de ubicación de una persona en un día. La consulta queda en auditoría.");
    }

    private static void MapDevices(IEndpointRouteBuilder app)
    {
        var devices = app.MapGroup("/api/v1/device-control").WithTags("Control de dispositivos").RequireAuthorization();

        devices.MapGet("/settings", async (IDeviceControlService service, CancellationToken ct) => Results.Ok(await service.GetSettingsAsync(ct)))
            .RequirePermission(Permission.ManageSettings).Produces<DeviceControlSettingsDto>().WithSummary("Configuración de «Apps corporativas solo en jornada».");

        devices.MapPut("/settings", async (SaveDeviceControlCommand command, IDeviceControlService service, CancellationToken ct) =>
                (await service.SaveSettingsAsync(command, ct)).ToHttpResult())
            .RequirePermission(Permission.ManageSettings).Produces<DeviceControlSettingsDto>()
            .WithSummary("Guarda la configuración. Activar exige verificación aprobada, alcance y avisos.");

        devices.MapPost("/verify", async (IDeviceControlService service, CancellationToken ct) => Results.Ok(await service.VerifyAsync(ct)))
            .RequirePermission(Permission.ManageSettings).Produces<VerificationDto>().WithSummary("Verifica los 5 puntos de MobiControl.");

        devices.MapGet("/devices", async (bool? unassigned, IDeviceControlService service, CancellationToken ct) =>
                Results.Ok(await service.ListDevicesAsync(unassigned ?? false, ct)))
            .RequireAnyPermission(Permission.ManageSettings, Permission.ManageUsers, Permission.ViewTeam)
            .Produces<IReadOnlyList<DeviceRowDto>>().WithSummary("Equipos de MobiControl con su persona, jornada y última orden.");

        devices.MapGet("/apps", async (IDeviceControlService service, CancellationToken ct) => Results.Ok(await service.ListAppsAsync(ct)))
            .RequirePermission(Permission.ManageSettings).Produces<IReadOnlyList<string>>().WithSummary("Catálogo de apps de MobiControl.");

        app.MapPut("/api/v1/members/{id:guid}/device", async (Guid id, AssignDeviceCommand command, IDeviceControlService service, CancellationToken ct) =>
                (await service.AssignAsync(id, command, ct)).ToHttpResult())
            .WithTags("Usuarios y roles").RequireAuthorization().RequirePermission(Permission.ManageUsers)
            .WithSummary("Asigna un dispositivo de MobiControl a la persona.");
    }
}
