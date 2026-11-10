using Microsoft.EntityFrameworkCore;
using WorkLog.Application.Abstractions;
using WorkLog.Domain.Access;

namespace WorkLog.Infrastructure.Persistence.Repositories;

/// <summary>
/// Sesiones de la app. Las búsquedas por token ignoran el filtro de empresa a propósito: el token es
/// lo que dice de qué empresa es la petición. El resumen del token tiene índice único.
/// </summary>
internal sealed class MobileSessionRepository(WorkLogDbContext db) : IMobileSessionRepository
{
    public void Add(MobileSession session) => db.MobileSessions.Add(session);

    public Task<MobileSession?> FindByAccessHashAsync(string accessHash, CancellationToken ct = default) =>
        db.MobileSessions.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.AccessTokenHash == accessHash, ct);

    public Task<MobileSession?> FindByRefreshHashAsync(string refreshHash, CancellationToken ct = default) =>
        db.MobileSessions.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.RefreshTokenHash == refreshHash || s.PreviousRefreshTokenHash == refreshHash, ct);

    public async Task<IReadOnlyList<MobileSession>> ListOpenAsync(Guid memberId, CancellationToken ct = default) =>
        await db.MobileSessions.Where(s => s.MemberId == memberId && s.RevokedAt == null).ToListAsync(ct);
}

/// <summary>Personas con contraseña de la app en todas las empresas, con el nombre de su empresa.</summary>
internal sealed class MobileLoginDirectory(WorkLogDbContext db) : IMobileLoginDirectory
{
    public async Task<IReadOnlyList<LoginCandidate>> FindByEmailAsync(string normalizedEmail, CancellationToken ct = default) =>
        await (from member in db.Members.IgnoreQueryFilters()
               join company in db.Companies on member.TenantId equals company.Id
               where member.Email == normalizedEmail
                     && member.Usage == MemberUsage.Mobile
                     && member.PasswordHash != null
                     && member.Status != MemberStatus.Inactive
               orderby company.Name
               select new LoginCandidate(member, company.Id, company.Name))
            .ToListAsync(ct);

    public Task<LoginCandidate?> FindAsync(Guid tenantId, Guid memberId, CancellationToken ct = default) =>
        (from member in db.Members.IgnoreQueryFilters()
         join company in db.Companies on member.TenantId equals company.Id
         where member.TenantId == tenantId && member.Id == memberId
         select new LoginCandidate(member, company.Id, company.Name))
        .FirstOrDefaultAsync(ct);
}
