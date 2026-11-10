using WorkLog.Domain.Common;

namespace WorkLog.Domain.Access;

/// <summary>Cómo usa WorkLog la persona: con la app en un dispositivo o solo desde el panel web.</summary>
public enum MemberUsage
{
    Mobile = 1,
    WebOnly = 2
}

public enum MemberStatus
{
    /// <summary>Invitada; pasa a activa la primera vez que ingresa con su cuenta de ONE.</summary>
    Invited = 1,
    Active = 2,
    Inactive = 3
}

/// <summary>
/// Persona dentro de WorkLog: rol, sede, grupo, supervisor y dispositivo. Quien usa la app tiene
/// su contraseña aquí (los usuarios móviles no tienen cuenta de ONE); el panel web entra con
/// INTECHSYS ONE, así que quien también lo usa tiene además su cuenta allá.
/// </summary>
public sealed class Member : TenantEntity
{
    private Member()
    {
    }

    private Member(Guid tenantId) : base(tenantId)
    {
    }

    /// <summary>Usuario de ONE. Null mientras la invitación no se ha usado.</summary>
    public Guid? OneUserId { get; private set; }

    public string Email { get; private set; } = string.Empty;
    public string FullName { get; private set; } = string.Empty;
    public MemberUsage Usage { get; private set; }
    public Guid RoleId { get; private set; }
    public Guid? BranchId { get; private set; }
    public Guid? WorkGroupId { get; private set; }
    public Guid? SupervisorId { get; private set; }
    public MemberStatus Status { get; private set; }
    public string? DeviceId { get; private set; }
    public string? DeviceModel { get; private set; }
    public string? PolicyVersion { get; private set; }
    public DateTimeOffset? PolicyAcceptedAt { get; private set; }
    public DateTimeOffset? LastSeenAt { get; private set; }

    /// <summary>Contraseña de la app (hash). Null: la persona aún no puede entrar a la app.</summary>
    public string? PasswordHash { get; private set; }

    /// <summary>La contraseña es temporal (la entregó un administrador): la app pide cambiarla al entrar.</summary>
    public bool MustChangePassword { get; private set; }

    public DateTimeOffset? PasswordSetAt { get; private set; }
    public int FailedLogins { get; private set; }
    public DateTimeOffset? LockedUntil { get; private set; }

    /// <summary>Intentos fallidos seguidos que bloquean la cuenta de la app.</summary>
    public const int MaxFailedLogins = 5;

    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    public bool HasAppPassword => PasswordHash is not null;

    public string Initials => Text.Initials(FullName);
    public bool IsActive => Status == MemberStatus.Active;

    public static Result<Member> Invite(Guid tenantId, MemberProfile profile)
    {
        var member = new Member(tenantId) { Status = MemberStatus.Invited };
        var applied = member.Apply(profile);
        return applied.IsSuccess ? member : applied.Error!;
    }

    /// <summary>
    /// Alta directa de quien ya administra la empresa en ONE (Owner o Admin): entra como
    /// administrador para poder configurar WorkLog la primera vez, sin que nadie lo invite.
    /// </summary>
    public static Member ProvisionAdministrator(Guid tenantId, Guid oneUserId, string email, string fullName, Guid adminRoleId, DateTimeOffset at) =>
        new(tenantId)
        {
            OneUserId = oneUserId,
            Email = NormalizeEmail(email),
            FullName = Text.Clean(fullName).Length == 0 ? NormalizeEmail(email) : Text.Clean(fullName),
            Usage = MemberUsage.WebOnly,
            RoleId = adminRoleId,
            Status = MemberStatus.Active,
            LastSeenAt = at
        };

    /// <summary>La cuenta de ONE ya existe (se creó o vinculó al invitar): queda ligada, aún como invitada.</summary>
    public void BindOneUser(Guid oneUserId)
    {
        if (OneUserId is null) OneUserId = oneUserId;
    }

    /// <summary>Primer ingreso: la invitación queda ligada al usuario de ONE que entró con ese correo.</summary>
    public Result LinkIdentity(Guid oneUserId, string? fullName, DateTimeOffset at)
    {
        if (Status == MemberStatus.Inactive)
            return DomainError.Forbidden("member.inactive", "Tu usuario de WorkLog está desactivado. Habla con tu administrador.");

        if (OneUserId is { } current && current != oneUserId)
            return DomainError.Conflict("member.identity_taken", "Este correo ya está ligado a otro usuario de ONE.");

        OneUserId = oneUserId;
        if (Text.CleanOrNull(fullName) is { } name) FullName = name;
        Status = MemberStatus.Active;
        LastSeenAt = at;
        return Result.Success();
    }

    public Result UpdateProfile(MemberProfile profile) => Apply(profile with { Email = Email });

    public void Touch(DateTimeOffset at) => LastSeenAt = at;

    public void Deactivate() => Status = MemberStatus.Inactive;

    /// <summary>Vuelve como estaba: activa si ya entró alguna vez (a la app o con ONE), invitada si no.</summary>
    public void Reactivate() => Status = OneUserId is null && LastSeenAt is null ? MemberStatus.Invited : MemberStatus.Active;

    /// <summary>Contraseña que entrega un administrador: sirve para entrar una vez y la app obliga a cambiarla.</summary>
    public Result SetTemporaryPassword(string passwordHash, DateTimeOffset at)
    {
        if (Usage != MemberUsage.Mobile)
            return DomainError.Validation("member.password_web", "Solo los usuarios de la app tienen contraseña de WorkLog; el panel entra con ONE.");

        PasswordHash = passwordHash;
        MustChangePassword = true;
        PasswordSetAt = at;
        FailedLogins = 0;
        LockedUntil = null;
        return Result.Success();
    }

    public void ChangePassword(string passwordHash, DateTimeOffset at)
    {
        PasswordHash = passwordHash;
        MustChangePassword = false;
        PasswordSetAt = at;
    }

    public bool IsLockedOut(DateTimeOffset now) => LockedUntil is { } until && until > now;

    /// <summary>Tras <see cref="MaxFailedLogins"/> intentos fallidos seguidos, la cuenta se bloquea un rato.</summary>
    public void RegisterFailedLogin(DateTimeOffset now)
    {
        FailedLogins++;
        if (FailedLogins < MaxFailedLogins) return;

        LockedUntil = now + LockoutDuration;
        FailedLogins = 0;
    }

    /// <summary>Ingreso correcto a la app. El primero activa la invitación.</summary>
    public void RegisterLogin(DateTimeOffset now)
    {
        FailedLogins = 0;
        LockedUntil = null;
        LastSeenAt = now;
        if (Status == MemberStatus.Invited) Status = MemberStatus.Active;
    }

    public Result AcceptPolicy(string version, DateTimeOffset at)
    {
        if (string.IsNullOrWhiteSpace(version))
            return DomainError.Validation("policy.version", "Falta la versión de la política.", "version");

        PolicyVersion = version.Trim();
        PolicyAcceptedAt = at;
        return Result.Success();
    }

    public bool HasAccepted(string currentVersion) => PolicyVersion == currentVersion;

    public Result AssignDevice(string deviceId, string? model)
    {
        if (Usage != MemberUsage.Mobile)
            return DomainError.Validation("member.device_web", "Una persona solo de panel web no lleva dispositivo.", "deviceId");

        var cleaned = Text.Clean(deviceId);
        if (cleaned.Length is 0 or > 120)
            return DomainError.Validation("member.device", "El identificador del dispositivo no es válido.", "deviceId");

        DeviceId = cleaned;
        DeviceModel = Text.CleanOrNull(model);
        return Result.Success();
    }

    /// <summary>Desvincula el dispositivo. Lo que estaba en cola en el teléfono sin sincronizar se pierde.</summary>
    public void UnlinkDevice()
    {
        DeviceId = null;
        DeviceModel = null;
    }

    private Result Apply(MemberProfile profile)
    {
        var name = Text.Clean(profile.FullName);
        if (Guard.Name(name, "fullName", 3, 150, "El nombre") is { } nameError) return nameError;

        var email = NormalizeEmail(profile.Email);
        if (!LooksLikeEmail(email))
            return DomainError.Validation("member.email", "Escribe un correo válido.", "email");

        if (profile.RoleId == Guid.Empty)
            return DomainError.Validation("member.role", "Elige el rol.", "roleId");

        if (profile.Usage == MemberUsage.Mobile && profile.BranchId is null)
            return DomainError.Validation("member.branch", "Un usuario móvil necesita una sede.", "branchId");

        FullName = name;
        Email = email;
        Usage = profile.Usage;
        RoleId = profile.RoleId;
        BranchId = profile.BranchId;
        WorkGroupId = profile.WorkGroupId;
        SupervisorId = profile.SupervisorId == Id ? null : profile.SupervisorId;

        if (Usage == MemberUsage.WebOnly)
        {
            // Sin app no hay dispositivo ni contraseña de WorkLog: el panel entra con ONE.
            UnlinkDevice();
            PasswordHash = null;
            MustChangePassword = false;
        }

        return Result.Success();
    }

    public static string NormalizeEmail(string? email) => Text.Clean(email).ToLowerInvariant();

    private static bool LooksLikeEmail(string email)
    {
        var at = email.IndexOf('@', StringComparison.Ordinal);
        return email.Length <= 200 && at > 0 && at == email.LastIndexOf('@') &&
               email.IndexOf('.', at) > at + 1 && !email.EndsWith('.') && !email.Contains(' ', StringComparison.Ordinal);
    }
}

public sealed record MemberProfile(
    string FullName,
    string Email,
    MemberUsage Usage,
    Guid RoleId,
    Guid? BranchId,
    Guid? WorkGroupId,
    Guid? SupervisorId);

/// <summary>Reglas que cruzan persona y rol.</summary>
public static class MemberRules
{
    /// <summary>En la app solo trabajan usuarios móviles y supervisores; un rol de oficina no lleva dispositivo.</summary>
    public static DomainError? ValidateRole(MemberUsage usage, Role role) => usage switch
    {
        MemberUsage.Mobile when role.SystemKey is not (SystemRoles.Mobile or SystemRoles.Supervisor) && role.Kind != RoleKind.Custom =>
            DomainError.Validation("member.role_usage", "Un usuario móvil solo puede ser Usuario móvil o Supervisor.", "roleId"),
        MemberUsage.WebOnly when role.SystemKey == SystemRoles.Mobile =>
            DomainError.Validation("member.role_usage", "Una persona solo de panel web no puede tener el rol Usuario móvil.", "roleId"),
        _ => null
    };
}
