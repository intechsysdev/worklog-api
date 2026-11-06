using WorkLog.Domain.Access;
using WorkLog.Domain.Auditing;

namespace WorkLog.Application.Abstractions;

/// <summary>Empresa sobre la que trabaja la petición. Lo usa la persistencia para filtrar por tenant.</summary>
public interface ITenantContext
{
    Guid? TenantId { get; }

    /// <summary>Persona que hace el cambio, para la traza de creación y modificación. Null en procesos del sistema.</summary>
    Guid? ActorId { get; }
}

/// <summary>Quién hace la petición, ya resuelto contra WorkLog (persona, rol y permisos).</summary>
public interface ICurrentActor
{
    Guid TenantId { get; }
    Guid MemberId { get; }
    Guid OneUserId { get; }
    string Name { get; }
    string RoleName { get; }

    /// <summary>Clave del rol del producto (admin, supervisor…), null en roles personalizados.</summary>
    string? RoleKey { get; }

    Permission Permissions { get; }

    /// <summary>Administrador de la plataforma ONE: puede editar parámetros globales y de país.</summary>
    bool IsPlatformAdmin { get; }

    AuditOrigin Origin { get; }

    bool Has(Permission permission) => (Permissions & permission) == permission;
}

/// <summary>Identidad del usuario tal como la reporta INTECHSYS ONE.</summary>
public sealed record OneIdentity(Guid UserId, string Email, string FullName, bool IsPlatformAdmin);

/// <summary>Empresa de ONE a la que llega el usuario con WorkLog asignado. Role null = acceso de plataforma.</summary>
public sealed record OneTenantAccess(Guid TenantId, string Name, string? Slug, string? Role)
{
    /// <summary>Owner y Admin de la empresa en ONE entran como administradores la primera vez.</summary>
    public bool CanAdministrate => Role is null || Role is "Owner" or "Admin";
}

/// <summary>Persona resuelta para una petición: lo que la capa de entrada necesita para autorizar.</summary>
public sealed record ActorInfo(
    Guid TenantId,
    string TenantName,
    Guid MemberId,
    Guid OneUserId,
    string Name,
    string Email,
    string RoleName,
    string? RoleKey,
    Permission Permissions,
    MemberUsage Usage,
    bool IsPlatformAdmin);
