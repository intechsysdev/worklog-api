using System.Text;
using WorkLog.Api.Extensions;
using WorkLog.Application.Abstractions;
using WorkLog.Application.Access;
using WorkLog.Application.Activities;
using WorkLog.Application.Common;
using WorkLog.Application.Organization;
using WorkLog.Application.Parameters;
using WorkLog.Application.Scheduling;
using WorkLog.Application.Workdays;
using WorkLog.Domain.Access;
using WorkLog.Domain.Activities;
using WorkLog.Domain.Parameters;

namespace WorkLog.Api.Endpoints;

/// <summary>Endpoints del panel web para supervisores y administradores.</summary>
public static class PanelEndpoints
{
    public static IEndpointRouteBuilder MapPanelEndpoints(this IEndpointRouteBuilder app)
    {
        MapTeam(app);
        MapOrganization(app);
        MapSites(app);
        MapSchedules(app);
        MapCatalog(app);
        MapWorkOrders(app);
        MapMembers(app);
        MapParameters(app);
        return app;
    }

    private static void MapTeam(IEndpointRouteBuilder app)
    {
        var team = app.MapGroup("/api/v1/team").WithTags("Equipo").RequireAuthorization().RequirePermission(Permission.ViewTeam);

        team.MapGet("/today", async (Guid? branchId, TeamFilter? filter, string? search, ITeamService service, CancellationToken ct) =>
                (await service.GetTodayAsync(branchId, filter ?? TeamFilter.All, search, ct)).ToHttpResult())
            .Produces<TeamTodayDto>().WithSummary("Equipo hoy: indicadores y estado de cada persona (actividad, pausa, sin actividad, sin iniciar).");

        var log = app.MapGroup("/api/v1/activity-log").WithTags("Registro de actividades").RequireAuthorization()
            .RequirePermission(Permission.ViewTeam);

        log.MapGet("/", async (DateOnly from, DateOnly to, Guid? memberId, Guid? branchId, IActivityLogService service, CancellationToken ct) =>
                (await service.ListAsync(from, to, memberId, branchId, ct)).ToHttpResult())
            .Produces<ActivityLogDto>()
            .WithSummary($"Jornadas de las personas en un rango (máx. {ActivityLog.MaxDays} días), con sus totales.");

        log.MapGet("/export", async (DateOnly from, DateOnly to, Guid? memberId, Guid? branchId, IActivityLogService service, CancellationToken ct) =>
            {
                var file = await service.ExportAsync(from, to, memberId, branchId, ct);
                return file.Succeeded ? Results.File(file.Value!.Content, file.Value.ContentType, file.Value.FileName) : file.ToHttpResult();
            })
            .WithSummary("Exporta el registro a Excel: una fila por actividad.");

        log.MapGet("/workdays/{id:guid}", async (Guid id, IActivityLogService service, CancellationToken ct) =>
                (await service.GetWorkdayAsync(id, ct)).ToHttpResult())
            .Produces<WorkdayDetailDto>().WithSummary("Detalle de una jornada: barra del día, actividades, pausas, resultados y evidencias.");

        log.MapGet("/evidence/{id:guid}", async (Guid id, IActivityLogService service, CancellationToken ct) =>
            {
                var file = await service.OpenEvidenceAsync(id, ct);
                return file.Succeeded ? Results.File(file.Value!.Content, file.Value.ContentType) : file.ToHttpResult();
            })
            .WithSummary("Foto o firma de una actividad.");
    }

    private static void MapOrganization(IEndpointRouteBuilder app)
    {
        var branches = app.MapGroup("/api/v1/branches").WithTags("Organización").RequireAuthorization();

        branches.MapGet("/", async (IOrganizationService service, CancellationToken ct) => Results.Ok(await service.ListBranchesAsync(ct)))
            .Produces<IReadOnlyList<BranchDto>>().WithSummary("Sedes con sus conteos de grupos y personas.");
        branches.MapPost("/", async (SaveBranchCommand command, IOrganizationService service, CancellationToken ct) =>
                (await service.CreateBranchAsync(command, ct)).ToCreatedResult(b => $"/api/v1/branches/{b.Id}"))
            .RequirePermission(Permission.ManageSettings).Produces<BranchDto>().WithSummary("Crea una sede (o la deja en borrador).");
        branches.MapPut("/{id:guid}", async (Guid id, SaveBranchCommand command, IOrganizationService service, CancellationToken ct) =>
                (await service.UpdateBranchAsync(id, command, ct)).ToHttpResult())
            .RequirePermission(Permission.ManageSettings).Produces<BranchDto>().WithSummary("Actualiza una sede.");
        branches.MapPost("/{id:guid}/active", async (Guid id, ActiveRequest request, IOrganizationService service, CancellationToken ct) =>
                (await service.SetBranchActiveAsync(id, request.Active, ct)).ToHttpResult())
            .RequirePermission(Permission.ManageSettings).Produces<BranchDto>().WithSummary("Activa o desactiva una sede (no se desactiva con personas).");

        var groups = app.MapGroup("/api/v1/groups").WithTags("Organización").RequireAuthorization();

        groups.MapGet("/", async (Guid? branchId, IOrganizationService service, CancellationToken ct) => Results.Ok(await service.ListGroupsAsync(branchId, ct)))
            .Produces<IReadOnlyList<WorkGroupDto>>().WithSummary("Grupos de trabajo.");
        groups.MapPost("/", async (SaveWorkGroupCommand command, IOrganizationService service, CancellationToken ct) =>
                (await service.CreateGroupAsync(command, ct)).ToCreatedResult(g => $"/api/v1/groups/{g.Id}"))
            .RequirePermission(Permission.ManageSettings).Produces<WorkGroupDto>().WithSummary("Crea un grupo.");
        groups.MapPut("/{id:guid}", async (Guid id, SaveWorkGroupCommand command, IOrganizationService service, CancellationToken ct) =>
                (await service.UpdateGroupAsync(id, command, ct)).ToHttpResult())
            .RequirePermission(Permission.ManageSettings).Produces<WorkGroupDto>().WithSummary("Actualiza un grupo.");
        groups.MapPost("/{id:guid}/active", async (Guid id, ActiveRequest request, IOrganizationService service, CancellationToken ct) =>
                (await service.SetGroupActiveAsync(id, request.Active, ct)).ToHttpResult())
            .RequirePermission(Permission.ManageSettings).Produces<WorkGroupDto>().WithSummary("Activa o desactiva un grupo.");
    }

    private static void MapSites(IEndpointRouteBuilder app)
    {
        var sites = app.MapGroup("/api/v1/sites").WithTags("Sitios").RequireAuthorization();

        sites.MapGet("/", async (bool? includeInactive, IOrganizationService service, CancellationToken ct) =>
                Results.Ok(await service.ListSitesAsync(includeInactive ?? false, ct)))
            .Produces<IReadOnlyList<WorkSiteDto>>().WithSummary("Sitios con su geocerca y las OT de la semana.");
        sites.MapPost("/", async (SaveWorkSiteCommand command, IOrganizationService service, CancellationToken ct) =>
                (await service.CreateSiteAsync(command, ct)).ToCreatedResult(s => $"/api/v1/sites/{s.Id}"))
            .RequirePermission(Permission.ManageSettings).Produces<WorkSiteDto>().WithSummary("Crea un sitio con geocerca circular o poligonal.");
        sites.MapPut("/{id:guid}", async (Guid id, SaveWorkSiteCommand command, IOrganizationService service, CancellationToken ct) =>
                (await service.UpdateSiteAsync(id, command, ct)).ToHttpResult())
            .RequirePermission(Permission.ManageSettings).Produces<WorkSiteDto>().WithSummary("Actualiza un sitio.");
        sites.MapPost("/{id:guid}/active", async (Guid id, ActiveRequest request, IOrganizationService service, CancellationToken ct) =>
                (await service.SetSiteActiveAsync(id, request.Active, ct)).ToHttpResult())
            .RequirePermission(Permission.ManageSettings).Produces<WorkSiteDto>().WithSummary("Activa o desactiva un sitio. Desactivar lo borra de MobiControl; activar lo vuelve a crear.");
        sites.MapPost("/mobicontrol-sync", async (IOrganizationService service, CancellationToken ct) =>
                (await service.SyncSitesToMobiControlAsync(null, ct)).ToHttpResult())
            .RequirePermission(Permission.ManageSettings).Produces<IReadOnlyList<WorkSiteDto>>()
            .WithSummary("Lleva a MobiControl todos los sitios (prefijo «WorkLog · »): crea, renombra o borra lo que falte.");
        sites.MapPost("/{id:guid}/mobicontrol-sync", async (Guid id, IOrganizationService service, CancellationToken ct) =>
                (await service.SyncSitesToMobiControlAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permission.ManageSettings).Produces<IReadOnlyList<WorkSiteDto>>()
            .WithSummary("Reintenta llevar un sitio a MobiControl.");
    }

    private static void MapSchedules(IEndpointRouteBuilder app)
    {
        var schedules = app.MapGroup("/api/v1/schedules").WithTags("Horarios").RequireAuthorization();

        schedules.MapGet("/templates", async (IScheduleService service, CancellationToken ct) => Results.Ok(await service.ListTemplatesAsync(ct)))
            .Produces<IReadOnlyList<ScheduleTemplateDto>>().WithSummary("Plantillas con su validación legal y uso.");
        schedules.MapPost("/templates/preview", async (SaveTemplateCommand command, IScheduleService service, CancellationToken ct) =>
                Results.Ok(await service.PreviewTemplateAsync(command, ct)))
            .RequirePermission(Permission.ManageSchedules).Produces<TemplatePreviewDto>().WithSummary("Valida una plantilla sin guardarla (para el editor).");
        schedules.MapPost("/templates", async (SaveTemplateCommand command, IScheduleService service, CancellationToken ct) =>
                (await service.CreateTemplateAsync(command, ct)).ToCreatedResult(t => $"/api/v1/schedules/templates/{t.Id}"))
            .RequirePermission(Permission.ManageSchedules).Produces<ScheduleTemplateDto>().WithSummary("Crea una plantilla. No se guarda si no cumple.");
        schedules.MapPut("/templates/{id:guid}", async (Guid id, SaveTemplateCommand command, IScheduleService service, CancellationToken ct) =>
                (await service.UpdateTemplateAsync(id, command, ct)).ToHttpResult())
            .RequirePermission(Permission.ManageSchedules).Produces<ScheduleTemplateDto>().WithSummary("Actualiza una plantilla.");
        schedules.MapPost("/templates/{id:guid}/active", async (Guid id, ActiveRequest request, IScheduleService service, CancellationToken ct) =>
                (await service.SetTemplateActiveAsync(id, request.Active, ct)).ToHttpResult())
            .RequirePermission(Permission.ManageSchedules).Produces<ScheduleTemplateDto>().WithSummary("Activa o desactiva una plantilla.");

        schedules.MapGet("/assignments", async (IScheduleService service, CancellationToken ct) => Results.Ok(await service.ListAssignmentsAsync(ct)))
            .Produces<IReadOnlyList<AssignmentDto>>().WithSummary("Asignaciones vigentes y futuras.");
        schedules.MapPost("/assignments", async (CreateAssignmentCommand command, IScheduleService service, CancellationToken ct) =>
                (await service.CreateAssignmentAsync(command, ct)).ToCreatedResult(a => $"/api/v1/schedules/assignments/{a.Id}"))
            .RequirePermission(Permission.ManageSchedules).Produces<AssignmentDto>().WithSummary("Asigna una plantilla a una sede, grupo o persona desde una fecha.");
        schedules.MapDelete("/assignments/{id:guid}", async (Guid id, IScheduleService service, CancellationToken ct) =>
                (await service.DeleteAssignmentAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permission.ManageSchedules).WithSummary("Elimina una asignación que aún no empieza.");

        schedules.MapGet("/calendar/{memberId:guid}", async (Guid memberId, int year, int month, IScheduleService service, CancellationToken ct) =>
                (await service.GetCalendarAsync(memberId, year, month, ct)).ToHttpResult())
            .RequireAnyPermission(Permission.ManageSchedules, Permission.ViewTeam)
            .Produces<MemberCalendarDto>().WithSummary("Calendario mensual de una persona: turno, novedad, cambio puntual, festivo o descanso.");
    }

    private static void MapCatalog(IEndpointRouteBuilder app)
    {
        var catalog = app.MapGroup("/api/v1/catalog").WithTags("Actividades").RequireAuthorization();

        catalog.MapGet("/", async (ICatalogService service, CancellationToken ct) => Results.Ok(await service.GetAsync(ct)))
            .Produces<CatalogDto>().WithSummary("Categorías, actividades, resultados de cierre y paleta de colores.");
        catalog.MapPost("/categories", async (SaveCategoryCommand command, ICatalogService service, CancellationToken ct) =>
                (await service.CreateCategoryAsync(command, ct)).ToCreatedResult(c => $"/api/v1/catalog/categories/{c.Id}"))
            .RequirePermission(Permission.ManageSettings).Produces<CategoryDto>().WithSummary("Crea una categoría.");
        catalog.MapPut("/categories/{id:guid}", async (Guid id, SaveCategoryCommand command, ICatalogService service, CancellationToken ct) =>
                (await service.UpdateCategoryAsync(id, command, ct)).ToHttpResult())
            .RequirePermission(Permission.ManageSettings).Produces<CategoryDto>().WithSummary("Actualiza una categoría.");
        catalog.MapPost("/categories/{id:guid}/active", async (Guid id, ActiveRequest request, ICatalogService service, CancellationToken ct) =>
                (await service.SetCategoryActiveAsync(id, request.Active, ct)).ToHttpResult())
            .RequirePermission(Permission.ManageSettings).Produces<CategoryDto>().WithSummary("Activa o desactiva una categoría (sin actividades activas).");
        catalog.MapPost("/activities", async (SaveActivityTypeCommand command, ICatalogService service, CancellationToken ct) =>
                (await service.CreateActivityAsync(command, ct)).ToCreatedResult(a => $"/api/v1/catalog/activities/{a.Id}"))
            .RequirePermission(Permission.ManageSettings).Produces<ActivityTypeDto>().WithSummary("Crea una actividad. Llega a la app en la siguiente sincronización.");
        catalog.MapPut("/activities/{id:guid}", async (Guid id, SaveActivityTypeCommand command, ICatalogService service, CancellationToken ct) =>
                (await service.UpdateActivityAsync(id, command, ct)).ToHttpResult())
            .RequirePermission(Permission.ManageSettings).Produces<ActivityTypeDto>().WithSummary("Actualiza una actividad.");
        catalog.MapPatch("/activities/{id:guid}", async (Guid id, PatchActivityTypeCommand command, ICatalogService service, CancellationToken ct) =>
                (await service.PatchActivityAsync(id, command, ct)).ToHttpResult())
            .RequirePermission(Permission.ManageSettings).Produces<ActivityTypeDto>().WithSummary("Cambia exigencias o estado de una actividad (interruptores de la tabla).");
        catalog.MapPut("/outcomes/{outcome}", async (ActivityOutcome outcome, OutcomeRequest request, ICatalogService service, CancellationToken ct) =>
                (await service.SetOutcomeObservationAsync(outcome, request.RequiresObservation, ct)).ToHttpResult())
            .RequirePermission(Permission.ManageSettings).Produces<OutcomeDto>().WithSummary("Define si un resultado de cierre pide observación.");
    }

    private static void MapWorkOrders(IEndpointRouteBuilder app)
    {
        var orders = app.MapGroup("/api/v1/work-orders").WithTags("Órdenes de trabajo").RequireAuthorization();

        orders.MapGet("/", async ([AsParameters] WorkOrderQuery query, IWorkOrderService service, CancellationToken ct) =>
                Results.Ok(await service.SearchAsync(new WorkOrderFilter(new PageRequest(query.Page ?? 1, query.PageSize ?? 20, query.Search),
                    query.From, query.To, query.AssigneeId, query.WorkSiteId, query.Status), ct)))
            .RequireAnyPermission(Permission.ManageSettings, Permission.ViewTeam)
            .Produces<PagedResult<WorkOrderDto>>().WithSummary("Busca OT por fecha, persona, sitio o estado.");
        orders.MapPost("/", async (SaveWorkOrderCommand command, IWorkOrderService service, CancellationToken ct) =>
                (await service.CreateAsync(command, ct)).ToCreatedResult(o => $"/api/v1/work-orders/{o.Id}"))
            .RequirePermission(Permission.ManageSettings).Produces<WorkOrderDto>().WithSummary("Crea una OT.");
        orders.MapPut("/{id:guid}", async (Guid id, SaveWorkOrderCommand command, IWorkOrderService service, CancellationToken ct) =>
                (await service.UpdateAsync(id, command, ct)).ToHttpResult())
            .RequirePermission(Permission.ManageSettings).Produces<WorkOrderDto>().WithSummary("Actualiza una OT.");
        orders.MapPost("/{id:guid}/cancel", async (Guid id, IWorkOrderService service, CancellationToken ct) =>
                (await service.CancelAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permission.ManageSettings).Produces<WorkOrderDto>().WithSummary("Cancela una OT.");
        orders.MapPost("/import", async (IFormFile file, IWorkOrderService service, CancellationToken ct) =>
            {
                if (file.Length is 0 or > 2 * 1024 * 1024)
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["file"] = ["El archivo debe pesar entre 1 byte y 2 MB."] },
                        detail: "El archivo debe pesar entre 1 byte y 2 MB.", title: "Datos inválidos");

                using var reader = new StreamReader(file.OpenReadStream(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                return (await service.ImportCsvAsync(reader, ct)).ToHttpResult();
            })
            .DisableAntiforgery()
            .RequirePermission(Permission.ManageSettings)
            .Produces<ImportReport>().WithSummary("Importa OT desde CSV: numero, actividad, sitio, tecnico, fecha, hora, notas.");
    }

    private static void MapMembers(IEndpointRouteBuilder app)
    {
        var members = app.MapGroup("/api/v1/members").WithTags("Usuarios y roles").RequireAuthorization();

        members.MapGet("/", async ([AsParameters] MemberQuery query, IMemberService service, CancellationToken ct) =>
                (await service.SearchAsync(new MemberFilter(new PageRequest(query.Page ?? 1, query.PageSize ?? 20, query.Search),
                    query.BranchId, query.WorkGroupId, query.RoleId, query.Status, query.Usage), ct)).ToHttpResult())
            .RequireAnyPermission(Permission.ManageUsers, Permission.ViewTeam, Permission.ManageSchedules, Permission.ManageSettings)
            .Produces<PagedResult<MemberDto>>().WithSummary("Busca personas por nombre, correo, sede, grupo, rol o estado.");
        members.MapGet("/{id:guid}", async (Guid id, IMemberService service, CancellationToken ct) => (await service.GetAsync(id, ct)).ToHttpResult())
            .RequireAnyPermission(Permission.ManageUsers, Permission.ViewTeam).Produces<MemberDto>().WithSummary("Detalle de una persona.");
        members.MapPost("/", async (SaveMemberCommand command, IMemberService service, CancellationToken ct) =>
                (await service.InviteAsync(command, ct)).ToCreatedResult(m => $"/api/v1/members/{m.Id}"))
            .RequirePermission(Permission.ManageUsers).Produces<MemberDto>().WithSummary("Invita a una persona. Pasa a activa cuando ingresa con su cuenta de ONE.");
        members.MapPut("/{id:guid}", async (Guid id, SaveMemberCommand command, IMemberService service, CancellationToken ct) =>
                (await service.UpdateAsync(id, command, ct)).ToHttpResult())
            .RequirePermission(Permission.ManageUsers).Produces<MemberDto>().WithSummary("Cambia rol, sede, grupo o supervisor.");
        members.MapPost("/{id:guid}/active", async (Guid id, ActiveRequest request, IMemberService service, CancellationToken ct) =>
                (await service.SetActiveAsync(id, request.Active, ct)).ToHttpResult())
            .RequirePermission(Permission.ManageUsers).Produces<MemberDto>().WithSummary("Activa o desactiva a una persona.");
        members.MapPost("/{id:guid}/app-password", async (Guid id, IMemberService service, CancellationToken ct) =>
                (await service.ResetAppPasswordAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permission.ManageUsers).Produces<AppPasswordDto>()
            .WithSummary("Genera una contraseña temporal de la app (se muestra una vez), cierra sus sesiones y obliga a cambiarla.");
        members.MapPost("/{id:guid}/one-account", async (Guid id, IMemberService service, CancellationToken ct) =>
                (await service.LinkOneAccountAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permission.ManageUsers).Produces<MemberDto>()
            .WithSummary("Crea o vincula la cuenta de ONE de un usuario de la app que también usa el panel.");
        members.MapDelete("/{id:guid}/device", async (Guid id, IMemberService service, CancellationToken ct) =>
                (await service.UnlinkDeviceAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permission.ManageUsers).WithSummary("Desvincula el dispositivo. Lo que estaba en cola sin sincronizar se pierde.");

        var roles = app.MapGroup("/api/v1/roles").WithTags("Usuarios y roles").RequireAuthorization();

        roles.MapGet("/", async (IMemberService service, CancellationToken ct) => (await service.ListRolesAsync(ct)).ToHttpResult())
            .Produces<IReadOnlyList<RoleDto>>().WithSummary("Roles con sus permisos y número de personas.");
        roles.MapGet("/permissions", (IMemberService service) => Results.Ok(service.ListPermissions()))
            .Produces<IReadOnlyList<PermissionDto>>().WithSummary("Catálogo de permisos agrupados.");
        roles.MapPost("/", async (CreateRoleCommand command, IMemberService service, CancellationToken ct) =>
                (await service.CreateRoleAsync(command, ct)).ToCreatedResult(r => $"/api/v1/roles/{r.Id}"))
            .RequirePermission(Permission.ManageUsers).Produces<RoleDto>().WithSummary("Crea un rol personalizado, opcionalmente copiando permisos.");
        roles.MapPut("/{id:guid}/permissions", async (Guid id, SetRolePermissionsCommand command, IMemberService service, CancellationToken ct) =>
                (await service.SetRolePermissionsAsync(id, command, ct)).ToHttpResult())
            .RequirePermission(Permission.ManageUsers).Produces<RoleDto>().WithSummary("Cambia los permisos de un rol (el Administrador no se limita).");
    }

    private static void MapParameters(IEndpointRouteBuilder app)
    {
        var parameters = app.MapGroup("/api/v1/parameters").WithTags("Parámetros").RequireAuthorization().RequirePermission(Permission.ManageSettings);

        parameters.MapGet("/{level}", async (ParameterLevel level, string? scope, IParameterService service, CancellationToken ct) =>
                (await service.GetAsync(level, scope, ct)).ToHttpResult())
            .Produces<IReadOnlyList<ParameterRowDto>>().WithSummary("Parámetros de un nivel (Global, Country, Company, Branch) con su valor efectivo y de dónde viene.");
        parameters.MapPut("/{level}/{code}", async (ParameterLevel level, string code, string? scope, SetParameterCommand command,
                IParameterService service, CancellationToken ct) => (await service.SetAsync(level, scope, code, command, ct)).ToHttpResult())
            .Produces<IReadOnlyList<ParameterRowDto>>().WithSummary("Cambia un parámetro desde una fecha. Los legales exigen norma o soporte.");
        parameters.MapDelete("/{level}/{code}", async (ParameterLevel level, string code, string? scope, IParameterService service, CancellationToken ct) =>
                (await service.ResetAsync(level, scope, code, ct)).ToHttpResult())
            .Produces<IReadOnlyList<ParameterRowDto>>().WithSummary("Vuelve a usar el valor heredado.");
    }
}

public sealed record ActiveRequest(bool Active);

public sealed record OutcomeRequest(bool RequiresObservation);

public sealed record WorkOrderQuery(
    int? Page,
    int? PageSize,
    string? Search,
    DateOnly? From,
    DateOnly? To,
    Guid? AssigneeId,
    Guid? WorkSiteId,
    WorkOrderStatus? Status);

public sealed record MemberQuery(
    int? Page,
    int? PageSize,
    string? Search,
    Guid? BranchId,
    Guid? WorkGroupId,
    Guid? RoleId,
    MemberStatus? Status,
    MemberUsage? Usage);
