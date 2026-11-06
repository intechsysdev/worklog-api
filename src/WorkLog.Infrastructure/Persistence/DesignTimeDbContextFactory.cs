using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using WorkLog.Application.Abstractions;

namespace WorkLog.Infrastructure.Persistence;

/// <summary>
/// Fábrica para las herramientas de EF (migraciones). No usa el contexto de una petición: la
/// cadena se toma de WORKLOG_DESIGN_CONNECTION o, si no está, de la base local de desarrollo.
/// </summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<WorkLogDbContext>
{
    public WorkLogDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("WORKLOG_DESIGN_CONNECTION")
                         ?? "Server=JEFO-PC;Database=WorkLog;Trusted_Connection=True;TrustServerCertificate=True";

        var options = new DbContextOptionsBuilder<WorkLogDbContext>().UseSqlServer(connection).Options;
        return new WorkLogDbContext(options, new NoTenant());
    }

    private sealed class NoTenant : ITenantContext
    {
        public Guid? TenantId => null;
        public Guid? ActorId => null;
    }
}
