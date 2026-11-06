using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using WorkLog.Application.Abstractions;
using WorkLog.Domain.Auditing;
using WorkLog.Domain.Common;

namespace WorkLog.Infrastructure.Persistence;

/// <summary>Confirma los cambios y traduce los choques de concurrencia a una excepción de la aplicación.</summary>
internal sealed class UnitOfWork(WorkLogDbContext db) : IUnitOfWork
{
    /// <summary>2601/2627: violación de índice o restricción única en SQL Server.</summary>
    private static readonly int[] UniqueViolations = [2601, 2627];

    public async Task SaveChangesAsync(CancellationToken ct = default)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            db.ChangeTracker.Clear();
            throw new PersistenceConflictException("Otro cambio se guardó al mismo tiempo.", ex);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException sql && UniqueViolations.Contains(sql.Number))
        {
            db.ChangeTracker.Clear();
            throw new PersistenceConflictException("El registro ya existe.", ex);
        }
    }
}

/// <summary>Completa quién y cuándo creó o modificó cada entidad auditable.</summary>
internal sealed class AuditFieldsInterceptor(ITenantContext tenant, TimeProvider clock) : SaveChangesInterceptor
{
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } context) Stamp(context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (eventData.Context is { } context) Stamp(context);
        return base.SavingChanges(eventData, result);
    }

    private void Stamp(DbContext context)
    {
        var now = clock.GetUtcNow();

        foreach (var entry in context.ChangeTracker.Entries())
        {
            // La auditoría es de solo agregar: un cambio o borrado es un error de programación.
            if (entry.Entity is AuditEvent && entry.State is EntityState.Modified or EntityState.Deleted)
                throw new InvalidOperationException("La auditoría no se puede modificar ni borrar.");

            if (entry.Entity is not AuditableEntity) continue;

            switch (entry.State)
            {
                case EntityState.Added:
                    entry.Property(nameof(AuditableEntity.CreatedAt)).CurrentValue = now;
                    entry.Property(nameof(AuditableEntity.CreatedBy)).CurrentValue = tenant.ActorId;
                    break;
                case EntityState.Modified:
                    entry.Property(nameof(AuditableEntity.UpdatedAt)).CurrentValue = now;
                    entry.Property(nameof(AuditableEntity.UpdatedBy)).CurrentValue = tenant.ActorId;
                    break;
            }
        }
    }
}
