using Microsoft.Extensions.Caching.Memory;
using WorkLog.Api.Auth;
using WorkLog.Api.Extensions;
using WorkLog.Application.Abstractions;
using WorkLog.Application.Access;
using WorkLog.Application.Activities;
using WorkLog.Application.Privacy;
using WorkLog.Application.Workdays;
using WorkLog.Domain.Access;
using WorkLog.Domain.Workdays;

namespace WorkLog.Api.Endpoints;

/// <summary>Lo propio de quien hace la petición: perfil, empresas, política y jornada (app móvil).</summary>
public static class MeEndpoints
{
    public static IEndpointRouteBuilder MapMeEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/time", (TimeProvider clock) => Results.Ok(new { serverTime = clock.GetUtcNow() }))
            .AllowAnonymous()
            .WithTags("Sistema")
            .WithSummary("Hora del servidor. La app la usa para mostrar la hora oficial y detectar relojes desfasados.");

        var me = app.MapGroup("/api/v1/me").WithTags("Mi cuenta").RequireAuthorization();

        me.MapGet("/tenants", async (HttpContext http, IIdentityProvider identities, ICompanyRepository companies, CancellationToken ct) =>
            {
                // La sesión de la app es de una sola empresa: la que dice la sesión.
                if (http.Items[OneClaims.AppSessionItem] is AppSession app)
                {
                    var company = await companies.FindAsync(app.TenantId, ct);
                    return Results.Ok(new List<OneTenantAccess> { new(app.TenantId, company?.Name ?? "Empresa", company?.Slug, "Member") });
                }

                if (http.Items[OneClaims.TokenItem] is not string token) return Results.Unauthorized();
                var tenants = await identities.GetTenantsAsync(token, ct);
                return tenants is null
                    ? Results.Problem("No se pudo consultar ONE.", statusCode: StatusCodes.Status503ServiceUnavailable)
                    : Results.Ok(tenants);
            })
            .WithoutTenant()
            .AllowPendingPasswordChange()
            .Produces<IReadOnlyList<OneTenantAccess>>().WithSummary("Empresas con WorkLog asignado a las que llega el usuario.");

        me.MapPost("/password", async (HttpContext http, ChangePasswordCommand command, IMobileAuthService auth, CancellationToken ct) =>
                http.Items[OneClaims.AppSessionItem] is AppSession app
                    ? (await auth.ChangePasswordAsync(app, command, ct)).ToHttpResult()
                    : Results.Problem("La contraseña del panel se cambia en INTECHSYS ONE.", title: "No aplica",
                        statusCode: StatusCodes.Status400BadRequest, extensions: new Dictionary<string, object?> { ["code"] = "password.one" }))
            .AllowPendingPasswordChange()
            .WithSummary("Cambia la contraseña de la app (usuarios con contraseña de WorkLog). Cierra las demás sesiones.");

        me.MapGet("/", async (IMemberService members, CancellationToken ct) => (await members.GetMyProfileAsync(ct)).ToHttpResult())
            .Produces<MyProfileDto>().WithSummary("Perfil de WorkLog: rol, permisos, sede, supervisor y dispositivo.");

        me.MapGet("/privacy-policy", async (IPrivacyService privacy, CancellationToken ct) => (await privacy.GetCurrentAsync(ct)).ToHttpResult())
            .Produces<PrivacyPolicyDto>().WithSummary("Política de tratamiento de datos vigente y si el usuario ya la aceptó.");

        me.MapPost("/privacy-policy/accept", async (AcceptPolicyCommand command, IPrivacyService privacy, CancellationToken ct) =>
                (await privacy.AcceptAsync(command, ct)).ToHttpResult())
            .Produces<PrivacyPolicyDto>().WithSummary("Acepta la versión vigente de la política. Queda en auditoría.");

        // ── Jornada (app móvil) ──────────────────────────────────────────────
        var work = me.MapGroup("/workday").WithTags("Mi jornada").RequirePermission(Permission.RecordWork);

        me.MapGet("/today", async (IMyWorkdayService workdays, CancellationToken ct) => (await workdays.GetTodayAsync(ct)).ToHttpResult())
            .WithTags("Mi jornada")
            .RequirePermission(Permission.ViewOwn)
            .Produces<TodayDto>().WithSummary("Turno de hoy, ventana de inicio, jornada en curso y parámetros para la app.");

        me.MapGet("/timeline", async (DateOnly? date, IMyWorkdayService workdays, CancellationToken ct) =>
                (await workdays.GetTimelineAsync(date, ct)).ToHttpResult())
            .WithTags("Mi jornada")
            .RequirePermission(Permission.ViewOwn)
            .Produces<TimelineDto>().WithSummary("Historial de un día: barra de distribución y línea de tiempo.");

        work.MapGet("/activity-options", async (IMyWorkdayService workdays, CancellationToken ct) =>
                (await workdays.GetActivityOptionsAsync(ct)).ToHttpResult())
            .Produces<ActivityOptionsDto>().WithSummary("OT asignadas hoy, catálogo de actividades y resultados de cierre.");

        work.MapPost("/start", async (StartWorkdayCommand command, IMyWorkdayService workdays, CancellationToken ct) =>
                (await workdays.StartWorkdayAsync(command, ct)).ToHttpResult())
            .Produces<WorkdayCommandResult>().WithSummary("Inicia la jornada. Valida turno, ventana de inicio y geocerca de la sede.");

        work.MapPost("/close", async (CloseWorkdayCommand command, IMyWorkdayService workdays, CancellationToken ct) =>
                (await workdays.CloseWorkdayAsync(command, ct)).ToHttpResult())
            .Produces<WorkdayCommandResult>().WithSummary("Cierra la jornada. La actividad abierta se finaliza como Completada.");

        work.MapPost("/activities", async (StartActivityCommand command, IMyWorkdayService workdays, CancellationToken ct) =>
                (await workdays.StartActivityAsync(command, ct)).ToHttpResult())
            .Produces<WorkdayCommandResult>().WithSummary("Inicia una actividad (de una OT o del catálogo). La anterior se finaliza sola.");

        work.MapPost("/activities/{activityId:guid}/pause", async (Guid activityId, ActivityEventCommand command, IMyWorkdayService workdays, CancellationToken ct) =>
                (await workdays.PauseActivityAsync(activityId, command, ct)).ToHttpResult())
            .Produces<WorkdayCommandResult>().WithSummary("Pausa la actividad en curso.");

        work.MapPost("/activities/{activityId:guid}/resume", async (Guid activityId, ActivityEventCommand command, IMyWorkdayService workdays, CancellationToken ct) =>
                (await workdays.ResumeActivityAsync(activityId, command, ct)).ToHttpResult())
            .Produces<WorkdayCommandResult>().WithSummary("Reanuda la actividad en pausa.");

        work.MapPost("/activities/{activityId:guid}/finish", async (Guid activityId, FinishActivityCommand command, IMyWorkdayService workdays, CancellationToken ct) =>
                (await workdays.FinishActivityAsync(activityId, command, ct)).ToHttpResult())
            .Produces<WorkdayCommandResult>().WithSummary("Finaliza la actividad con resultado, observación y evidencias declaradas.");

        work.MapPost("/activities/{activityId:guid}/evidences", async (
                Guid activityId, [Microsoft.AspNetCore.Mvc.FromForm] EvidenceForm form, IMyWorkdayService workdays, CancellationToken ct) =>
            {
                if (form.File is null || form.File.Length == 0)
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["file"] = ["Adjunta la imagen."] });

                await using var stream = form.File.OpenReadStream();
                var result = await workdays.UploadEvidenceAsync(new UploadEvidenceCommand(
                    form.ClientId, activityId, form.Kind, form.CapturedAt, stream, form.File.ContentType, form.File.Length), ct);
                return result.ToHttpResult();
            })
            .DisableAntiforgery()
            .Produces<EvidenceDto>().WithSummary("Sube una foto o la firma del cliente (multipart). Reintentar con el mismo clientId no duplica.");

        work.MapPost("/locations", async (LocationSamplesCommand command, IMyWorkdayService workdays, CancellationToken ct) =>
                (await workdays.RecordLocationsAsync(command, ct)).ToHttpResult(stored => Results.Ok(new { stored })))
            .WithSummary("Puntos del muestreo periódico. Fuera de la jornada se descartan.");

        work.MapPost("/sync", async (SyncBatchRequest request, ISyncService sync, CancellationToken ct) =>
                (await sync.ProcessAsync(request.Events, ct)).ToHttpResult())
            .Produces<SyncBatchResult>().WithSummary("Cola del teléfono: procesa los eventos en orden e informa el resultado de cada uno.");

        return app;
    }
}

public sealed record SyncBatchRequest(IReadOnlyList<SyncEvent>? Events);

public sealed class EvidenceForm
{
    public Guid ClientId { get; set; }
    public EvidenceKind Kind { get; set; } = EvidenceKind.Photo;
    public DateTimeOffset? CapturedAt { get; set; }
    public IFormFile? File { get; set; }
}
