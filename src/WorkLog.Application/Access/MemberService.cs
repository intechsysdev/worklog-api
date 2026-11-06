using WorkLog.Application.Abstractions;
using WorkLog.Application.Common;
using WorkLog.Domain.Access;
using WorkLog.Domain.Common;

namespace WorkLog.Application.Access;

public sealed record MemberDto(
    Guid Id,
    string FullName,
    string Initials,
    string Email,
    MemberUsage Usage,
    MemberStatus Status,
    Guid RoleId,
    string RoleName,
    Guid? BranchId,
    string? BranchName,
    Guid? WorkGroupId,
    string? WorkGroupName,
    Guid? SupervisorId,
    string? SupervisorName,
    string? DeviceId,
    string? DeviceModel,
    string? PolicyVersion,
    DateTimeOffset? PolicyAcceptedAt,
    DateTimeOffset? LastSeenAt,
    bool IsLinkedToOne)
{
    /// <summary>Solo al invitar: se creó la cuenta en ONE ahora.</summary>
    public bool? OneAccountCreated { get; init; }

    /// <summary>Solo al invitar y si la cuenta se creó: contraseña temporal para entregar a la persona (una sola vez).</summary>
    public string? TemporaryPassword { get; init; }

    /// <summary>Solo al invitar: aviso cuando ONE no permitió el alta y quedó solo la invitación de WorkLog.</summary>
    public string? OneNotice { get; init; }
}

public sealed record SaveMemberCommand(
    string? FullName,
    string? Email,
    MemberUsage Usage,
    Guid RoleId,
    Guid? BranchId,
    Guid? WorkGroupId,
    Guid? SupervisorId);

public sealed record RoleDto(
    Guid Id,
    string Name,
    RoleKind Kind,
    string? SystemKey,
    IReadOnlyList<string> Permissions,
    int People);

public sealed record PermissionDto(string Key, string Group, string Name, string Description);

public sealed record CreateRoleCommand(string? Name, Guid? CopyFromRoleId);

public sealed record SetRolePermissionsCommand(IReadOnlyList<string>? Permissions);

/// <summary>Perfil de quien hace la petición: lo que el panel y la app necesitan al arrancar.</summary>
public sealed record MyProfileDto(
    Guid MemberId,
    Guid TenantId,
    string TenantName,
    string FullName,
    string Initials,
    string Email,
    string RoleName,
    string? RoleKey,
    MemberUsage Usage,
    IReadOnlyList<string> Permissions,
    bool IsPlatformAdmin,
    Guid? BranchId,
    string? BranchName,
    string? WorkGroupName,
    string? SupervisorName,
    string? DeviceId,
    string? DeviceModel,
    string? PolicyVersion,
    DateTimeOffset? PolicyAcceptedAt);

public interface IMemberService
{
    Task<OperationResult<MyProfileDto>> GetMyProfileAsync(CancellationToken ct = default);

    Task<OperationResult<PagedResult<MemberDto>>> SearchAsync(MemberFilter filter, CancellationToken ct = default);

    Task<OperationResult<MemberDto>> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>Invita a una persona. Queda "Invitada" hasta que ingrese con su cuenta de ONE.</summary>
    Task<OperationResult<MemberDto>> InviteAsync(SaveMemberCommand command, CancellationToken ct = default);

    Task<OperationResult<MemberDto>> UpdateAsync(Guid id, SaveMemberCommand command, CancellationToken ct = default);

    Task<OperationResult<MemberDto>> SetActiveAsync(Guid id, bool active, CancellationToken ct = default);

    Task<OperationResult> UnlinkDeviceAsync(Guid id, CancellationToken ct = default);

    Task<OperationResult<IReadOnlyList<RoleDto>>> ListRolesAsync(CancellationToken ct = default);

    IReadOnlyList<PermissionDto> ListPermissions();

    Task<OperationResult<RoleDto>> CreateRoleAsync(CreateRoleCommand command, CancellationToken ct = default);

    Task<OperationResult<RoleDto>> SetRolePermissionsAsync(Guid roleId, SetRolePermissionsCommand command, CancellationToken ct = default);
}

internal sealed class MemberService(
    ICurrentActor actor,
    IMemberRepository members,
    IRoleRepository roles,
    IBranchRepository branches,
    IWorkGroupRepository groups,
    ICompanyRepository companies,
    IIdentityProvider identities,
    ICallerCredentials caller,
    IAuditTrail audit,
    IUnitOfWork unitOfWork) : IMemberService
{
    public async Task<OperationResult<MyProfileDto>> GetMyProfileAsync(CancellationToken ct = default)
    {
        var member = await members.FindAsync(actor.MemberId, ct);
        if (member is null) return OperationResult<MyProfileDto>.NotFound();

        var role = await roles.FindAsync(member.RoleId, ct);
        var company = await companies.FindAsync(actor.TenantId, ct);
        var branch = member.BranchId is { } b ? await branches.FindAsync(b, ct) : null;
        var group = member.WorkGroupId is { } g ? await groups.FindAsync(g, ct) : null;
        var supervisor = member.SupervisorId is { } s ? await members.FindAsync(s, ct) : null;

        return new MyProfileDto(
            member.Id,
            actor.TenantId,
            company?.Name ?? string.Empty,
            member.FullName,
            member.Initials,
            member.Email,
            role?.Name ?? string.Empty,
            role?.SystemKey,
            member.Usage,
            PermissionKeys(actor.Permissions),
            actor.IsPlatformAdmin,
            member.BranchId,
            branch?.Name,
            group?.Name,
            supervisor?.FullName,
            member.DeviceId,
            member.DeviceModel,
            member.PolicyVersion,
            member.PolicyAcceptedAt);
    }

    public async Task<OperationResult<PagedResult<MemberDto>>> SearchAsync(MemberFilter filter, CancellationToken ct = default)
    {
        var page = await members.SearchAsync(filter, ct);
        var lookups = await LoadLookupsAsync(page.Items, ct);
        return page.Map(m => ToDto(m, lookups));
    }

    public async Task<OperationResult<MemberDto>> GetAsync(Guid id, CancellationToken ct = default)
    {
        var member = await members.FindAsync(id, ct);
        if (member is null) return OperationResult<MemberDto>.NotFound("La persona no existe.");
        return ToDto(member, await LoadLookupsAsync([member], ct));
    }

    public async Task<OperationResult<MemberDto>> InviteAsync(SaveMemberCommand command, CancellationToken ct = default)
    {
        var email = Member.NormalizeEmail(command.Email);
        if (await members.FindByEmailAsync(email, ct) is { } existing)
            return OperationResult<MemberDto>.Invalid($"Ya existe un usuario con este correo: {existing.FullName}.", "email", "member.email_taken");

        var validation = await ValidateReferencesAsync(command, null, ct);
        if (!validation.Succeeded) return OperationResult<MemberDto>.From(validation);

        var created = Member.Invite(actor.TenantId, ToProfile(command));
        if (created.IsFailure) return OperationResult<MemberDto>.From(created.Error!);

        // La identidad vive en ONE: se crea (o se vincula) allá con el token de quien invita. Si ONE no
        // lo permite (quien invita no administra la empresa en ONE), queda la invitación de WorkLog y
        // la persona entra en cuanto un administrador de ONE la agregue a la empresa.
        var member = created.Value;
        OneProvisionResult? provisioned = null;
        if (caller.AccessToken is { } token)
        {
            var (first, last) = SplitName(member.FullName);
            provisioned = await identities.ProvisionMemberAsync(token, actor.TenantId, member.Email, first, last, ct);
            if (provisioned is { Succeeded: true, UserId: { } oneUserId })
            {
                if (await members.FindByOneUserAsync(oneUserId, ct) is { } taken)
                    return OperationResult<MemberDto>.Invalid($"Esa cuenta de ONE ya está en WorkLog como {taken.FullName}.", "email", "member.one_taken");
                member.BindOneUser(oneUserId);
            }
            else if (provisioned is { Succeeded: false, Forbidden: false })
            {
                return OperationResult<MemberDto>.Conflict($"ONE no pudo dar de alta a la persona: {provisioned.Error}", "one.provision_failed");
            }
        }

        members.Add(member);
        audit.Record("member.invited",
            $"Invitó a {member.FullName} ({member.Email}){(provisioned?.UserCreated == true ? " y creó su cuenta en ONE" : "")}.",
            "Usuarios", member.Id.ToString(), after: new { member.Email, member.Usage, command.RoleId });
        await unitOfWork.SaveChangesAsync(ct);

        var dto = await GetAsync(member.Id, ct);
        return dto.Succeeded
            ? dto.Value! with
            {
                OneAccountCreated = provisioned?.Succeeded == true ? provisioned.UserCreated : null,
                TemporaryPassword = provisioned?.TemporaryPassword,
                OneNotice = provisioned is { Forbidden: true }
                    ? "Tu usuario no administra la empresa en ONE: la persona podrá entrar cuando un administrador de ONE la agregue a la empresa."
                    : null
            }
            : dto;
    }

    public async Task<OperationResult<MemberDto>> UpdateAsync(Guid id, SaveMemberCommand command, CancellationToken ct = default)
    {
        var member = await members.FindAsync(id, ct);
        if (member is null) return OperationResult<MemberDto>.NotFound("La persona no existe.");

        var validation = await ValidateReferencesAsync(command, member.Id, ct);
        if (!validation.Succeeded) return OperationResult<MemberDto>.From(validation);

        if (member.Id == actor.MemberId && member.RoleId != command.RoleId)
            return OperationResult<MemberDto>.Invalid("No puedes cambiar tu propio rol.", "roleId", "member.own_role");

        var before = new { member.RoleId, member.BranchId, member.WorkGroupId, member.SupervisorId, member.Usage };
        var updated = member.UpdateProfile(ToProfile(command));
        if (updated.IsFailure) return OperationResult<MemberDto>.From(updated.Error!);

        audit.Record("member.updated", $"Actualizó los datos de {member.FullName}.", "Usuarios", member.Id.ToString(),
            before, new { member.RoleId, member.BranchId, member.WorkGroupId, member.SupervisorId, member.Usage });
        await unitOfWork.SaveChangesAsync(ct);

        return await GetAsync(member.Id, ct);
    }

    public async Task<OperationResult<MemberDto>> SetActiveAsync(Guid id, bool active, CancellationToken ct = default)
    {
        var member = await members.FindAsync(id, ct);
        if (member is null) return OperationResult<MemberDto>.NotFound("La persona no existe.");

        if (member.Id == actor.MemberId && !active)
            return OperationResult<MemberDto>.Invalid("No puedes desactivar tu propio usuario.", code: "member.self_deactivate");

        if (active) member.Reactivate();
        else member.Deactivate();

        audit.Record(active ? "member.activated" : "member.deactivated",
            $"{(active ? "Activó" : "Desactivó")} a {member.FullName}.", "Usuarios", member.Id.ToString());
        await unitOfWork.SaveChangesAsync(ct);
        return await GetAsync(member.Id, ct);
    }

    public async Task<OperationResult> UnlinkDeviceAsync(Guid id, CancellationToken ct = default)
    {
        var member = await members.FindAsync(id, ct);
        if (member is null) return OperationResult.NotFound("La persona no existe.");
        if (member.DeviceId is null) return OperationResult.Ok();

        var device = member.DeviceId;
        member.UnlinkDevice();
        audit.Record("member.device_unlinked", $"Desvinculó el dispositivo {device} de {member.FullName}.", "Dispositivos", member.Id.ToString(),
            new { DeviceId = device }, null);
        await unitOfWork.SaveChangesAsync(ct);
        return OperationResult.Ok();
    }

    public async Task<OperationResult<IReadOnlyList<RoleDto>>> ListRolesAsync(CancellationToken ct = default)
    {
        var list = await roles.ListAsync(ct);
        var counts = await members.CountPerRoleAsync(ct);
        return list
            .OrderBy(r => r.Kind == RoleKind.Custom)
            .ThenBy(r => r.Name)
            .Select(r => ToDto(r, counts.GetValueOrDefault(r.Id)))
            .ToList();
    }

    public IReadOnlyList<PermissionDto> ListPermissions() =>
        PermissionInfo.Catalog.Select(p => new PermissionDto(p.Key, p.Group, p.Name, p.Description)).ToList();

    public async Task<OperationResult<RoleDto>> CreateRoleAsync(CreateRoleCommand command, CancellationToken ct = default)
    {
        if (await roles.NameExistsAsync(Text.Key(command.Name), null, ct))
            return OperationResult<RoleDto>.Invalid("Ya existe un rol con ese nombre.", "name", "role.name_taken");

        Role? source = null;
        if (command.CopyFromRoleId is { } copyId && (source = await roles.FindAsync(copyId, ct)) is null)
            return OperationResult<RoleDto>.Invalid("El rol a copiar no existe.", "copyFromRoleId");

        var created = Role.CreateCustom(actor.TenantId, command.Name ?? string.Empty, source);
        if (created.IsFailure) return OperationResult<RoleDto>.From(created.Error!);

        roles.Add(created.Value);
        audit.Record("role.created", $"Creó el rol {created.Value.Name}{(source is null ? "" : $" copiando {source.Name}")}.",
            "Permisos", created.Value.Id.ToString(), after: new { Permissions = PermissionKeys(created.Value.Permissions) });
        await unitOfWork.SaveChangesAsync(ct);
        return ToDto(created.Value, 0);
    }

    public async Task<OperationResult<RoleDto>> SetRolePermissionsAsync(Guid roleId, SetRolePermissionsCommand command, CancellationToken ct = default)
    {
        var role = await roles.FindAsync(roleId, ct);
        if (role is null) return OperationResult<RoleDto>.NotFound("El rol no existe.");

        var permissions = Permission.None;
        foreach (var key in command.Permissions ?? [])
        {
            if (PermissionInfo.FromKey(key) is not { } info)
                return OperationResult<RoleDto>.Invalid($"El permiso «{key}» no existe.", "permissions");
            permissions |= info.Permission;
        }

        var before = PermissionKeys(role.Permissions);
        var updated = role.SetPermissions(permissions);
        if (updated.IsFailure) return OperationResult<RoleDto>.From(updated.Error!);

        audit.Record("role.permissions_changed", $"Cambió los permisos del rol {role.Name}.", "Permisos", role.Id.ToString(),
            new { Permissions = before }, new { Permissions = PermissionKeys(role.Permissions) });
        await unitOfWork.SaveChangesAsync(ct);

        var counts = await members.CountPerRoleAsync(ct);
        return ToDto(role, counts.GetValueOrDefault(role.Id));
    }

    private async Task<OperationResult> ValidateReferencesAsync(SaveMemberCommand command, Guid? memberId, CancellationToken ct)
    {
        var role = await roles.FindAsync(command.RoleId, ct);
        if (role is null) return OperationResult.Invalid("Elige el rol.", "roleId");

        if (MemberRules.ValidateRole(command.Usage, role) is { } roleError) return OperationResult.From(roleError);

        if (command.BranchId is { } branchId && await branches.FindAsync(branchId, ct) is not { IsActive: true })
            return OperationResult.Invalid("Elige una sede activa.", "branchId");

        if (command.WorkGroupId is { } groupId)
        {
            var group = await groups.FindAsync(groupId, ct);
            if (group is not { IsActive: true }) return OperationResult.Invalid("Elige un grupo activo.", "workGroupId");
            if (command.BranchId is { } b && group.BranchId != b)
                return OperationResult.Invalid("El grupo no pertenece a la sede elegida.", "workGroupId");
        }

        if (command.SupervisorId is { } supervisorId)
        {
            if (supervisorId == memberId) return OperationResult.Invalid("Una persona no puede ser su propio supervisor.", "supervisorId");
            if (await members.FindAsync(supervisorId, ct) is not { IsActive: true })
                return OperationResult.Invalid("Elige un supervisor activo.", "supervisorId");
        }

        return OperationResult.Ok();
    }

    private static (string First, string Last) SplitName(string fullName)
    {
        var parts = fullName.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 ? (parts[0], parts[1]) : (fullName, string.Empty);
    }

    private static MemberProfile ToProfile(SaveMemberCommand c) =>
        new(c.FullName ?? string.Empty, c.Email ?? string.Empty, c.Usage, c.RoleId, c.BranchId, c.WorkGroupId, c.SupervisorId);

    private sealed record Lookups(
        IReadOnlyDictionary<Guid, string> Roles,
        IReadOnlyDictionary<Guid, string> Branches,
        IReadOnlyDictionary<Guid, string> Groups,
        IReadOnlyDictionary<Guid, string> Supervisors);

    private async Task<Lookups> LoadLookupsAsync(IReadOnlyCollection<Member> list, CancellationToken ct)
    {
        var roleList = await roles.ListAsync(ct);
        var branchList = await branches.ListAsync(ct);
        var groupList = await groups.ListAsync(null, ct);
        var supervisorIds = list.Where(m => m.SupervisorId is not null).Select(m => m.SupervisorId!.Value).Distinct().ToList();
        var supervisors = await members.FindManyAsync(supervisorIds, ct);

        return new Lookups(
            roleList.ToDictionary(r => r.Id, r => r.Name),
            branchList.ToDictionary(b => b.Id, b => b.Name),
            groupList.ToDictionary(g => g.Id, g => g.Name),
            supervisors.ToDictionary(s => s.Id, s => s.FullName));
    }

    private static MemberDto ToDto(Member m, Lookups l) => new(
        m.Id,
        m.FullName,
        m.Initials,
        m.Email,
        m.Usage,
        m.Status,
        m.RoleId,
        l.Roles.GetValueOrDefault(m.RoleId, string.Empty),
        m.BranchId,
        m.BranchId is { } b ? l.Branches.GetValueOrDefault(b) : null,
        m.WorkGroupId,
        m.WorkGroupId is { } g ? l.Groups.GetValueOrDefault(g) : null,
        m.SupervisorId,
        m.SupervisorId is { } s ? l.Supervisors.GetValueOrDefault(s) : null,
        m.DeviceId,
        m.DeviceModel,
        m.PolicyVersion,
        m.PolicyAcceptedAt,
        m.LastSeenAt,
        m.OneUserId is not null);

    private static RoleDto ToDto(Role r, int people) =>
        new(r.Id, r.Name, r.Kind, r.SystemKey, PermissionKeys(r.EffectivePermissions), people);

    internal static IReadOnlyList<string> PermissionKeys(Permission permissions) =>
        PermissionInfo.Catalog.Where(p => (permissions & p.Permission) == p.Permission).Select(p => p.Key).ToList();
}
