using WorkLog.Domain.Common;

namespace WorkLog.Application.Common;

/// <summary>Clasificación del fallo, para traducirlo al código HTTP correcto en la capa de entrada.</summary>
public enum ErrorKind
{
    None = 0,
    Validation = 1,
    NotFound = 2,
    Conflict = 3,
    Forbidden = 4,
    Unauthorized = 5
}

/// <summary>Resultado de un caso de uso, sin excepciones para el flujo esperado.</summary>
public class OperationResult
{
    protected OperationResult(bool succeeded, ErrorKind kind, string? message, string? code, IReadOnlyDictionary<string, string[]>? errors)
    {
        Succeeded = succeeded;
        Kind = kind;
        Message = message;
        Code = code;
        Errors = errors ?? new Dictionary<string, string[]>();
    }

    public bool Succeeded { get; }
    public ErrorKind Kind { get; }
    public string? Message { get; }

    /// <summary>Código estable del error (p. ej. "workday.too_early") para que el cliente decida qué mostrar.</summary>
    public string? Code { get; }

    public IReadOnlyDictionary<string, string[]> Errors { get; }

    public static OperationResult Ok() => new(true, ErrorKind.None, null, null, null);

    public static OperationResult Fail(ErrorKind kind, string message, string? code = null, IReadOnlyDictionary<string, string[]>? errors = null) =>
        new(false, kind, message, code, errors);

    public static OperationResult NotFound(string message = "No se encontró el recurso solicitado.") => Fail(ErrorKind.NotFound, message);

    public static OperationResult Conflict(string message, string? code = null) => Fail(ErrorKind.Conflict, message, code);

    public static OperationResult Invalid(string message, string? field = null, string? code = null) =>
        Fail(ErrorKind.Validation, message, code, field is null ? null : new Dictionary<string, string[]> { [field] = [message] });

    public static OperationResult Forbidden(string message = "No tiene permisos sobre este recurso.", string? code = null) =>
        Fail(ErrorKind.Forbidden, message, code);

    public static OperationResult From(DomainError error) =>
        Fail(Map(error.Type), error.Message, error.Code, FieldErrors(error));

    public static OperationResult From(Result result) => result.IsSuccess ? Ok() : From(result.Error!);

    protected static ErrorKind Map(ErrorType type) => type switch
    {
        ErrorType.Conflict => ErrorKind.Conflict,
        ErrorType.NotFound => ErrorKind.NotFound,
        ErrorType.Forbidden => ErrorKind.Forbidden,
        _ => ErrorKind.Validation
    };

    protected static IReadOnlyDictionary<string, string[]>? FieldErrors(DomainError error) =>
        error.Field is null ? null : new Dictionary<string, string[]> { [error.Field] = [error.Message] };
}

/// <summary>Resultado de un caso de uso que devuelve un valor.</summary>
public sealed class OperationResult<T> : OperationResult
{
    private OperationResult(bool succeeded, T? value, ErrorKind kind, string? message, string? code, IReadOnlyDictionary<string, string[]>? errors)
        : base(succeeded, kind, message, code, errors) => Value = value;

    public T? Value { get; }

    public static OperationResult<T> Ok(T value) => new(true, value, ErrorKind.None, null, null, null);

    public static new OperationResult<T> Fail(ErrorKind kind, string message, string? code = null, IReadOnlyDictionary<string, string[]>? errors = null) =>
        new(false, default, kind, message, code, errors);

    public static new OperationResult<T> NotFound(string message = "No se encontró el recurso solicitado.") => Fail(ErrorKind.NotFound, message);

    public static new OperationResult<T> Conflict(string message, string? code = null) => Fail(ErrorKind.Conflict, message, code);

    public static new OperationResult<T> Invalid(string message, string? field = null, string? code = null) =>
        Fail(ErrorKind.Validation, message, code, field is null ? null : new Dictionary<string, string[]> { [field] = [message] });

    public static new OperationResult<T> Forbidden(string message = "No tiene permisos sobre este recurso.", string? code = null) =>
        Fail(ErrorKind.Forbidden, message, code);

    public static new OperationResult<T> From(DomainError error) =>
        Fail(Map(error.Type), error.Message, error.Code, FieldErrors(error));

    /// <summary>Propaga el fallo de otro resultado con un tipo de valor distinto.</summary>
    public static OperationResult<T> From(OperationResult failed) =>
        new(false, default, failed.Kind, failed.Message, failed.Code, failed.Errors);

    public static implicit operator OperationResult<T>(T value) => Ok(value);
}
