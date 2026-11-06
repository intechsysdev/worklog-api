using WorkLog.Application.Abstractions;
using WorkLog.Application.Common;
using WorkLog.Domain.Access;
using WorkLog.Domain.Activities;
using WorkLog.Domain.Organization;

namespace WorkLog.Application.Access;

/// <summary>Puerto de entrada: convierte la identidad de ONE en una persona de WorkLog con permisos.</summary>
public interface IAccessService
{
    Task<OperationResult<ActorInfo>> ResolveAsync(OneIdentity identity, OneTenantAccess tenant, CancellationToken ct = default);
}

/// <summary>
/// Resuelve a la persona en cada petición. La primera vez que alguien de una empresa entra, crea
/// lo que la empresa necesita para empezar (roles, categorías, resultados). Quien administra la
/// empresa en ONE entra como Administrador; los demás necesitan una invitación en WorkLog.
/// </summary>
internal sealed class AccessService(
    ICompanyRepository companies,
    IRoleRepository roles,
    IMemberRepository members,
    IActivityCategoryRepository categories,
    IOutcomeSettingRepository outcomes,
    IAuditStore audit,
    IUnitOfWork unitOfWork,
    TimeProvider clock) : IAccessService
{
    /// <summary>Último ingreso: se actualiza como mucho cada 5 minutos para no escribir en cada petición.</summary>
    private static readonly TimeSpan TouchInterval = TimeSpan.FromMinutes(5);

    public async Task<OperationResult<ActorInfo>> ResolveAsync(OneIdentity identity, OneTenantAccess tenant, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var company = await EnsureCompanyAsync(tenant, ct);

        var member = await members.FindByOneUserAsync(identity.UserId, ct);
        var changed = false;

        if (member is { Status: MemberStatus.Invited })
        {
            var activated = member.LinkIdentity(identity.UserId, identity.FullName, now);
            if (activated.IsFailure) return OperationResult<ActorInfo>.From(activated.Error!);
            changed = true;
        }

        if (member is null && await members.FindByEmailAsync(Member.NormalizeEmail(identity.Email), ct) is { } invited)
        {
            var linked = invited.LinkIdentity(identity.UserId, identity.FullName, now);
            if (linked.IsFailure) return OperationResult<ActorInfo>.From(linked.Error!);

            member = invited;
            changed = true;
            audit.Add(Domain.Auditing.AuditEvent.Create(new Domain.Auditing.AuditEntry(
                tenant.TenantId, now, null, member.Id, member.FullName, null, Domain.Auditing.AuditOrigin.System,
                "member.activated", $"{member.FullName} ingresó por primera vez y quedó activo.", "Usuarios", member.Id.ToString())));
        }

        if (member is null)
        {
            if (!tenant.CanAdministrate && !identity.IsPlatformAdmin)
            {
                return OperationResult<ActorInfo>.Forbidden(
                    $"No tienes acceso a WorkLog en {company.Name}. Pide a tu administrador que te invite.", "access.not_invited");
            }

            var admin = await roles.FindBySystemKeyAsync(SystemRoles.Administrator, ct)
                        ?? throw new InvalidOperationException("La empresa no tiene rol Administrador.");

            member = Member.ProvisionAdministrator(tenant.TenantId, identity.UserId, identity.Email, identity.FullName, admin.Id, now);
            members.Add(member);
            changed = true;
            audit.Add(Domain.Auditing.AuditEvent.Create(new Domain.Auditing.AuditEntry(
                tenant.TenantId, now, null, member.Id, member.FullName, admin.Name, Domain.Auditing.AuditOrigin.System,
                "member.provisioned", $"{member.FullName} entró como Administrador por administrar la empresa en ONE.",
                "Usuarios", member.Id.ToString())));
        }

        if (member.Status == MemberStatus.Inactive)
            return OperationResult<ActorInfo>.Forbidden("Tu usuario de WorkLog está desactivado. Habla con tu administrador.", "access.inactive");

        var role = await roles.FindAsync(member.RoleId, ct)
                   ?? throw new InvalidOperationException($"El rol {member.RoleId} no existe.");

        if (member.LastSeenAt is null || now - member.LastSeenAt > TouchInterval)
        {
            member.Touch(now);
            changed = true;
        }

        if (changed) await unitOfWork.SaveChangesAsync(ct);

        return new ActorInfo(
            tenant.TenantId,
            company.Name,
            member.Id,
            identity.UserId,
            member.FullName,
            member.Email,
            role.Name,
            role.SystemKey,
            role.EffectivePermissions,
            member.Usage,
            identity.IsPlatformAdmin);
    }

    /// <summary>Alta y refresco de la empresa. Los catálogos iniciales se crean una sola vez.</summary>
    private async Task<Company> EnsureCompanyAsync(OneTenantAccess tenant, CancellationToken ct)
    {
        var company = await companies.FindAsync(tenant.TenantId, ct);

        if (company is null)
        {
            company = Company.Register(tenant.TenantId, tenant.Name, tenant.Slug);
            companies.Add(company);
        }
        else
        {
            company.Refresh(tenant.Name, tenant.Slug);
        }

        if (!company.IsProvisioned)
        {
            foreach (var role in DefaultRoles.For(tenant.TenantId)) roles.Add(role);
            foreach (var category in ActivityCategory.Defaults(tenant.TenantId)) categories.Add(category);
            foreach (var outcome in OutcomeSetting.Defaults(tenant.TenantId)) outcomes.Add(outcome);
            company.MarkProvisioned();
            await unitOfWork.SaveChangesAsync(ct);
        }

        return company;
    }
}
