namespace WorkLog.Domain.Common;

/// <summary>Base de toda entidad: identidad estable con Guid v7 (ordenable en el índice).</summary>
public abstract class Entity
{
    protected Entity() => Id = Guid.CreateVersion7();

    public Guid Id { get; protected set; }
}

/// <summary>Entidad con traza de creación y modificación. La completa la persistencia, no el dominio.</summary>
public abstract class AuditableEntity : Entity
{
    public DateTimeOffset CreatedAt { get; private set; }
    public Guid? CreatedBy { get; private set; }
    public DateTimeOffset? UpdatedAt { get; private set; }
    public Guid? UpdatedBy { get; private set; }
}

/// <summary>Dato que pertenece a una empresa de INTECHSYS ONE.</summary>
public interface ITenantOwned
{
    Guid TenantId { get; }
}

/// <summary>Entidad auditable que vive dentro de una empresa (tenant de ONE).</summary>
public abstract class TenantEntity : AuditableEntity, ITenantOwned
{
    protected TenantEntity()
    {
    }

    protected TenantEntity(Guid tenantId)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("La empresa es obligatoria.", nameof(tenantId));

        TenantId = tenantId;
    }

    public Guid TenantId { get; private set; }
}
