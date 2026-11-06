using WorkLog.Domain.Common;

namespace WorkLog.Domain.Access;

public enum RoleKind
{
    /// <summary>No se puede limitar (Administrador).</summary>
    Fixed = 1,

    /// <summary>Viene con el producto; sus permisos se pueden ajustar.</summary>
    Base = 2,

    /// <summary>Creado por la empresa copiando otro rol.</summary>
    Custom = 3
}

/// <summary>Claves de los roles que trae el producto. Las reglas de uso (móvil o panel) se apoyan en ellas.</summary>
public static class SystemRoles
{
    public const string Mobile = "mobile";
    public const string Supervisor = "supervisor";
    public const string HumanResources = "hr";
    public const string Payroll = "payroll";
    public const string Administrator = "admin";
}

/// <summary>Rol de WorkLog dentro de una empresa, con su conjunto de permisos.</summary>
public sealed class Role : TenantEntity
{
    private Role()
    {
    }

    private Role(Guid tenantId) : base(tenantId)
    {
    }

    public string Name { get; private set; } = string.Empty;
    public string NameKey { get; private set; } = string.Empty;
    public RoleKind Kind { get; private set; }
    public string? SystemKey { get; private set; }
    public Permission Permissions { get; private set; }

    public bool IsAdministrator => SystemKey == SystemRoles.Administrator;

    /// <summary>Permisos efectivos: el rol fijo los tiene todos aunque la columna diga otra cosa.</summary>
    public Permission EffectivePermissions => Kind == RoleKind.Fixed ? Permission.All : Permissions;

    public bool Has(Permission permission) => (EffectivePermissions & permission) == permission;

    public static Role CreateSystem(Guid tenantId, string systemKey, string name, RoleKind kind, Permission permissions) =>
        new(tenantId)
        {
            Name = name,
            NameKey = Text.Key(name),
            Kind = kind,
            SystemKey = systemKey,
            Permissions = kind == RoleKind.Fixed ? Permission.All : permissions
        };

    public static Result<Role> CreateCustom(Guid tenantId, string name, Role? copyFrom)
    {
        var cleaned = Text.Clean(name);
        if (Guard.Name(cleaned, "name", 3, 80, "El nombre del rol") is { } error) return error;

        if (copyFrom is { Kind: RoleKind.Fixed })
            return DomainError.Validation("role.copy_fixed", "No se pueden copiar los permisos de un rol fijo.", "copyFromRoleId");

        return new Role(tenantId)
        {
            Name = cleaned,
            NameKey = Text.Key(cleaned),
            Kind = RoleKind.Custom,
            Permissions = copyFrom?.Permissions ?? Permission.ViewOwn
        };
    }

    public Result Rename(string name)
    {
        if (Kind != RoleKind.Custom)
            return DomainError.Validation("role.rename_system", "Los roles del producto no se renombran.");

        var cleaned = Text.Clean(name);
        if (Guard.Name(cleaned, "name", 3, 80, "El nombre del rol") is { } error) return error;

        Name = cleaned;
        NameKey = Text.Key(cleaned);
        return Result.Success();
    }

    public Result SetPermissions(Permission permissions)
    {
        if (Kind == RoleKind.Fixed)
            return DomainError.Validation("role.fixed", "El rol Administrador tiene todos los permisos y no se puede limitar.");

        // Ver lo propio es la base de cualquier rol: sin él la persona no ve ni su jornada.
        Permissions = (permissions & Permission.All) | Permission.ViewOwn;
        return Result.Success();
    }
}

/// <summary>Matriz de permisos con la que nace cada empresa.</summary>
public static class DefaultRoles
{
    public static IReadOnlyList<Role> For(Guid tenantId) =>
    [
        Role.CreateSystem(tenantId, SystemRoles.Mobile, "Usuario móvil", RoleKind.Base,
            Permission.ViewOwn | Permission.RecordWork),
        // El supervisor puede registrar su propia jornada en la app si es usuario móvil (solo esos inician jornada).
        Role.CreateSystem(tenantId, SystemRoles.Supervisor, "Supervisor", RoleKind.Base,
            Permission.ViewOwn | Permission.RecordWork | Permission.ViewTeam | Permission.ViewLocations | Permission.ApproveRequests | Permission.ClosePeriods),
        Role.CreateSystem(tenantId, SystemRoles.HumanResources, "Recursos humanos", RoleKind.Base,
            Permission.ViewOwn | Permission.ViewTeam | Permission.ManageAbsences | Permission.ManageSchedules | Permission.ManageUsers),
        Role.CreateSystem(tenantId, SystemRoles.Payroll, "Nómina", RoleKind.Base,
            Permission.ViewOwn | Permission.ViewTeam | Permission.ClosePeriods | Permission.ExportPayroll),
        Role.CreateSystem(tenantId, SystemRoles.Administrator, "Administrador", RoleKind.Fixed, Permission.All)
    ];
}
