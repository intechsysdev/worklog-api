namespace WorkLog.Domain.Common;

/// <summary>Tipo de fallo de una regla de negocio, para que la capa de entrada elija el código HTTP.</summary>
public enum ErrorType
{
    Validation = 1,
    Conflict = 2,
    NotFound = 3,
    Forbidden = 4
}

/// <summary>Regla de negocio incumplida. <see cref="Field"/> señala el dato que el usuario debe corregir.</summary>
public sealed record DomainError(string Code, string Message, string? Field = null, ErrorType Type = ErrorType.Validation)
{
    public static DomainError Validation(string code, string message, string? field = null) => new(code, message, field);
    public static DomainError Conflict(string code, string message, string? field = null) => new(code, message, field, ErrorType.Conflict);
    public static DomainError NotFound(string code, string message) => new(code, message, null, ErrorType.NotFound);
    public static DomainError Forbidden(string code, string message) => new(code, message, null, ErrorType.Forbidden);
}

/// <summary>Resultado de una operación de dominio sin excepciones para el flujo esperado.</summary>
public class Result
{
    protected Result(DomainError? error) => Error = error;

    public DomainError? Error { get; }
    public bool IsSuccess => Error is null;
    public bool IsFailure => Error is not null;

    public static Result Success() => new(null);
    public static Result Failure(DomainError error) => new(error);

    public static implicit operator Result(DomainError error) => Failure(error);
}

/// <summary>Resultado que entrega un valor cuando la operación tiene éxito.</summary>
public sealed class Result<T> : Result
{
    private readonly T? _value;

    private Result(T value) : base(null) => _value = value;
    private Result(DomainError error) : base(error) => _value = default;

    /// <summary>Valor de una operación exitosa. Leerlo tras un fallo es un error de programación.</summary>
    public T Value => IsSuccess ? _value! : throw new InvalidOperationException($"Resultado fallido: {Error!.Code}.");

    public static Result<T> Success(T value) => new(value);
    public static new Result<T> Failure(DomainError error) => new(error);

    public static implicit operator Result<T>(T value) => Success(value);
    public static implicit operator Result<T>(DomainError error) => Failure(error);
}
