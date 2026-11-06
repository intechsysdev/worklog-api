using WorkLog.Application.Abstractions;
using WorkLog.Domain.Access;
using WorkLog.Domain.Auditing;

namespace WorkLog.Api.Auth;

/// <summary>
/// Contexto de la petición: empresa y persona resueltas por <see cref="TenantResolutionMiddleware"/>.
/// Implementa los puertos que leen la aplicación (actor) y la persistencia (empresa).
/// </summary>
public sealed class RequestContext : ITenantContext, ICurrentActor, ICallerCredentials
{
    public string? AccessToken { get; private set; }

    internal void SetAccessToken(string token) => AccessToken = token;

    private ActorInfo? _actor;

    public Guid? TenantId { get; private set; }

    public Guid? ActorId => _actor?.MemberId;

    public AuditOrigin Origin { get; private set; } = AuditOrigin.Web;

    public ActorInfo Actor => _actor ?? throw new InvalidOperationException("La petición no tiene persona resuelta.");

    public bool HasActor => _actor is not null;

    Guid ICurrentActor.TenantId => Actor.TenantId;
    public Guid MemberId => Actor.MemberId;
    public Guid OneUserId => Actor.OneUserId;
    public string Name => Actor.Name;
    public string RoleName => Actor.RoleName;
    public string? RoleKey => Actor.RoleKey;
    public Permission Permissions => Actor.Permissions;
    public bool IsPlatformAdmin => Actor.IsPlatformAdmin;

    public bool Has(Permission permission) => _actor is not null && (_actor.Permissions & permission) == permission;

    internal void SetTenant(Guid tenantId) => TenantId = tenantId;

    internal void SetActor(ActorInfo actor) => _actor = actor;

    internal void SetOrigin(AuditOrigin origin) => Origin = origin;

    /// <summary>Contexto de un proceso automático: empresa sin persona, origen Sistema.</summary>
    internal void SetSystem(Guid tenantId)
    {
        TenantId = tenantId;
        Origin = AuditOrigin.System;
    }
}
