namespace WorkLog.Application.Abstractions;

/// <summary>
/// La persistencia rechazó el cambio porque otra petición escribió lo mismo a la vez (clave
/// única o control de concurrencia). La infraestructura traduce su excepción a esta.
/// </summary>
public sealed class PersistenceConflictException : Exception
{
    public PersistenceConflictException()
    {
    }

    public PersistenceConflictException(string message) : base(message)
    {
    }

    public PersistenceConflictException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
