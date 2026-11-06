using Microsoft.AspNetCore.Mvc;
using WorkLog.Api.Auth;
using WorkLog.Application.Common;
using WorkLog.Domain.Access;

namespace WorkLog.Api.Extensions;

/// <summary>Traduce el resultado de aplicación al código HTTP y al ProblemDetails correspondiente.</summary>
public static class ResultExtensions
{
    public static IResult ToHttpResult<T>(this OperationResult<T> result, Func<T, IResult>? onSuccess = null) =>
        result.Succeeded ? onSuccess?.Invoke(result.Value!) ?? Results.Ok(result.Value) : Problem(result);

    public static IResult ToHttpResult(this OperationResult result) =>
        result.Succeeded ? Results.NoContent() : Problem(result);

    public static IResult ToCreatedResult<T>(this OperationResult<T> result, Func<T, string> location) =>
        result.Succeeded ? Results.Created(location(result.Value!), result.Value) : Problem(result);

    private static IResult Problem(OperationResult result)
    {
        var status = result.Kind switch
        {
            ErrorKind.Validation => StatusCodes.Status400BadRequest,
            ErrorKind.Unauthorized => StatusCodes.Status401Unauthorized,
            ErrorKind.Forbidden => StatusCodes.Status403Forbidden,
            ErrorKind.NotFound => StatusCodes.Status404NotFound,
            ErrorKind.Conflict => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status400BadRequest
        };

        var title = result.Kind switch
        {
            ErrorKind.Validation => "Datos inválidos",
            ErrorKind.Unauthorized => "No autenticado",
            ErrorKind.Forbidden => "Acceso denegado",
            ErrorKind.NotFound => "No encontrado",
            ErrorKind.Conflict => "Conflicto",
            _ => "Solicitud incorrecta"
        };

        // "code" permite al cliente reaccionar a un caso concreto (p. ej. workday.too_early) sin leer el texto.
        var extensions = result.Code is null ? null : new Dictionary<string, object?> { ["code"] = result.Code };

        return result.Errors.Count > 0
            ? Results.ValidationProblem(result.Errors.ToDictionary(e => e.Key, e => e.Value), result.Message, title: title,
                statusCode: status, extensions: extensions)
            : Results.Problem(result.Message, title: title, statusCode: status, extensions: extensions);
    }
}

/// <summary>Autorización por permiso de WorkLog (los permisos dependen del rol en la empresa de la petición).</summary>
public static class PermissionExtensions
{
    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, Permission permission) where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(async (context, next) =>
        {
            var request = context.HttpContext.RequestServices.GetRequiredService<RequestContext>();
            if (!request.HasActor) return Results.Unauthorized();

            return request.Has(permission)
                ? await next(context)
                : Results.Problem("Tu rol no tiene permiso para esta acción.", title: "Acceso denegado",
                    statusCode: StatusCodes.Status403Forbidden,
                    extensions: new Dictionary<string, object?> { ["code"] = "access.permission", ["permission"] = permission.ToString() });
        });

    /// <summary>Cualquiera de los permisos indicados basta.</summary>
    public static TBuilder RequireAnyPermission<TBuilder>(this TBuilder builder, params Permission[] permissions) where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(async (context, next) =>
        {
            var request = context.HttpContext.RequestServices.GetRequiredService<RequestContext>();
            if (!request.HasActor) return Results.Unauthorized();

            return permissions.Any(request.Has)
                ? await next(context)
                : Results.Problem("Tu rol no tiene permiso para esta acción.", title: "Acceso denegado",
                    statusCode: StatusCodes.Status403Forbidden, extensions: new Dictionary<string, object?> { ["code"] = "access.permission" });
        });

    /// <summary>Endpoint autenticado que no requiere empresa resuelta.</summary>
    public static TBuilder WithoutTenant<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new SkipTenantResolution());
}

/// <summary>Traduce una excepción no controlada a ProblemDetails sin filtrar detalles internos.</summary>
public sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger, IHostEnvironment environment)
    : Microsoft.AspNetCore.Diagnostics.IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var context = httpContext;
        var ct = cancellationToken;
        // Un cuerpo mal formado es culpa del cliente: un 500 lo llevaría a reintentar algo que nunca funcionará.
        if (exception is BadHttpRequestException badRequest)
        {
            logger.LogWarning(exception, "Solicitud mal formada en {Method} {Path}", context.Request.Method, context.Request.Path);
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new ProblemDetails
            {
                Title = "Solicitud incorrecta",
                Status = StatusCodes.Status400BadRequest,
                Detail = "El cuerpo de la solicitud no es válido: revisa el formato de los campos (fechas aaaa-mm-dd, horas hh:mm:ss, ids).",
                Instance = context.Request.Path,
                Extensions = environment.IsDevelopment()
                    ? new Dictionary<string, object?> { ["code"] = "request.malformed", ["debug"] = badRequest.Message }
                    : new Dictionary<string, object?> { ["code"] = "request.malformed" }
            }, ct);
            return true;
        }

        if (exception is Application.Abstractions.PersistenceConflictException)
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            await context.Response.WriteAsJsonAsync(new ProblemDetails
            {
                Title = "Conflicto",
                Status = StatusCodes.Status409Conflict,
                Detail = "Otro cambio se guardó al mismo tiempo. Recarga e inténtalo de nuevo.",
                Instance = context.Request.Path
            }, ct);
            return true;
        }

        logger.LogError(exception, "Error no controlado en {Method} {Path}", context.Request.Method, context.Request.Path);
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Title = "Error interno",
            Status = StatusCodes.Status500InternalServerError,
            Detail = environment.IsDevelopment() ? exception.ToString() : "Ocurrió un error procesando la solicitud.",
            Instance = context.Request.Path
        }, ct);
        return true;
    }
}
