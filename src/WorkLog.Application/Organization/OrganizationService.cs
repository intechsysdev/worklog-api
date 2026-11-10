using WorkLog.Application.Abstractions;
using WorkLog.Application.Common;
using WorkLog.Domain.Common;
using WorkLog.Domain.Geo;
using WorkLog.Domain.Organization;

namespace WorkLog.Application.Organization;

public sealed record BranchDto(
    Guid Id,
    string Name,
    string CountryCode,
    string City,
    string? Region,
    string TimeZoneId,
    Guid? MainSiteId,
    string? MainSiteName,
    Guid? DefaultTemplateId,
    string? DefaultTemplateName,
    BranchStatus Status,
    int Groups,
    int People);

public sealed record SaveBranchCommand(
    string? Name,
    string? CountryCode,
    string? City,
    string? Region,
    string? TimeZoneId,
    Guid? MainSiteId,
    Guid? DefaultTemplateId,
    bool AsDraft = false);

public sealed record WorkGroupDto(
    Guid Id,
    string Name,
    Guid BranchId,
    string BranchName,
    Guid? SupervisorId,
    string? SupervisorName,
    Guid? DefaultTemplateId,
    string? DefaultTemplateName,
    string? MdmGroupPath,
    bool IsActive,
    int People);

public sealed record SaveWorkGroupCommand(
    string? Name,
    Guid BranchId,
    Guid? SupervisorId,
    Guid? DefaultTemplateId,
    string? MdmGroupPath);

public sealed record GeoPointDto(double Latitude, double Longitude);

public sealed record WorkSiteDto(
    Guid Id,
    string Name,
    string? Address,
    GeofenceShape Shape,
    double Latitude,
    double Longitude,
    int RadiusMeters,
    IReadOnlyList<GeoPointDto> Vertices,
    bool IsActive,
    int WorkOrdersThisWeek,
    string? MobiControlName,
    DateTimeOffset? MobiControlSyncedAt,
    string? MobiControlError);

public sealed record SaveWorkSiteCommand(
    string? Name,
    string? Address,
    GeofenceShape Shape,
    double Latitude,
    double Longitude,
    int? RadiusMeters,
    IReadOnlyList<GeoPointDto>? Vertices);

public interface IOrganizationService
{
    Task<IReadOnlyList<BranchDto>> ListBranchesAsync(CancellationToken ct = default);
    Task<OperationResult<BranchDto>> CreateBranchAsync(SaveBranchCommand command, CancellationToken ct = default);
    Task<OperationResult<BranchDto>> UpdateBranchAsync(Guid id, SaveBranchCommand command, CancellationToken ct = default);
    Task<OperationResult<BranchDto>> SetBranchActiveAsync(Guid id, bool active, CancellationToken ct = default);

    Task<IReadOnlyList<WorkGroupDto>> ListGroupsAsync(Guid? branchId, CancellationToken ct = default);
    Task<OperationResult<WorkGroupDto>> CreateGroupAsync(SaveWorkGroupCommand command, CancellationToken ct = default);
    Task<OperationResult<WorkGroupDto>> UpdateGroupAsync(Guid id, SaveWorkGroupCommand command, CancellationToken ct = default);
    Task<OperationResult<WorkGroupDto>> SetGroupActiveAsync(Guid id, bool active, CancellationToken ct = default);

    Task<IReadOnlyList<WorkSiteDto>> ListSitesAsync(bool includeInactive, CancellationToken ct = default);
    Task<OperationResult<WorkSiteDto>> CreateSiteAsync(SaveWorkSiteCommand command, CancellationToken ct = default);
    Task<OperationResult<WorkSiteDto>> UpdateSiteAsync(Guid id, SaveWorkSiteCommand command, CancellationToken ct = default);
    Task<OperationResult<WorkSiteDto>> SetSiteActiveAsync(Guid id, bool active, CancellationToken ct = default);

    /// <summary>Vuelve a llevar a MobiControl un sitio (o todos, con <paramref name="id"/> null).</summary>
    Task<OperationResult<IReadOnlyList<WorkSiteDto>>> SyncSitesToMobiControlAsync(Guid? id, CancellationToken ct = default);
}

internal sealed class OrganizationService(
    ICurrentActor actor,
    IBranchRepository branches,
    IWorkGroupRepository groups,
    IWorkSiteRepository sites,
    IMemberRepository members,
    IScheduleTemplateRepository templates,
    IWorkOrderRepository workOrders,
    Parameters.IParameterProvider parameters,
    IAuditTrail audit,
    IUnitOfWork unitOfWork,
    TimeProvider clock,
    ISiteGeofenceSync geofences) : IOrganizationService
{
    // ── Sedes ───────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<BranchDto>> ListBranchesAsync(CancellationToken ct = default)
    {
        var list = await branches.ListAsync(ct);
        var groupList = await groups.ListAsync(null, ct);
        var people = await members.ListAllAsync(ct);
        var siteNames = (await sites.ListAsync(true, ct)).ToDictionary(s => s.Id, s => s.Name);
        var templateNames = (await templates.ListAsync(ct)).ToDictionary(t => t.Id, t => t.Name);

        return list
            .OrderBy(b => b.Status == BranchStatus.Inactive)
            .ThenBy(b => b.Name)
            .Select(b => ToDto(b, siteNames, templateNames,
                groupList.Count(g => g.BranchId == b.Id && g.IsActive),
                people.Count(m => m.BranchId == b.Id && m.Status != Domain.Access.MemberStatus.Inactive)))
            .ToList();
    }

    public async Task<OperationResult<BranchDto>> CreateBranchAsync(SaveBranchCommand command, CancellationToken ct = default)
    {
        var check = await ValidateBranchAsync(command, null, ct);
        if (!check.Succeeded) return OperationResult<BranchDto>.From(check);

        var created = Branch.Create(actor.TenantId, ToData(command), command.AsDraft);
        if (created.IsFailure) return OperationResult<BranchDto>.From(created.Error!);

        var branch = created.Value;
        branch.SetMainSite(command.MainSiteId);
        branch.SetDefaultTemplate(command.DefaultTemplateId);
        branches.Add(branch);

        audit.Record("branch.created", $"Creó la sede {branch.Name}.", "Organización", branch.Id.ToString(),
            after: new { branch.Name, branch.CountryCode, branch.City, branch.TimeZoneId });
        await unitOfWork.SaveChangesAsync(ct);
        return await BranchDtoAsync(branch, ct);
    }

    public async Task<OperationResult<BranchDto>> UpdateBranchAsync(Guid id, SaveBranchCommand command, CancellationToken ct = default)
    {
        var branch = await branches.FindAsync(id, ct);
        if (branch is null) return OperationResult<BranchDto>.NotFound("La sede no existe.");

        var check = await ValidateBranchAsync(command, id, ct);
        if (!check.Succeeded) return OperationResult<BranchDto>.From(check);

        var before = new { branch.Name, branch.CountryCode, branch.City, branch.TimeZoneId, branch.MainSiteId, branch.DefaultTemplateId };
        var updated = branch.Update(ToData(command));
        if (updated.IsFailure) return OperationResult<BranchDto>.From(updated.Error!);

        branch.SetMainSite(command.MainSiteId);
        branch.SetDefaultTemplate(command.DefaultTemplateId);
        if (!command.AsDraft && branch.Status == BranchStatus.Draft) branch.Activate();

        audit.Record("branch.updated", $"Actualizó la sede {branch.Name}.", "Organización", branch.Id.ToString(), before,
            new { branch.Name, branch.CountryCode, branch.City, branch.TimeZoneId, branch.MainSiteId, branch.DefaultTemplateId });
        await unitOfWork.SaveChangesAsync(ct);
        return await BranchDtoAsync(branch, ct);
    }

    public async Task<OperationResult<BranchDto>> SetBranchActiveAsync(Guid id, bool active, CancellationToken ct = default)
    {
        var branch = await branches.FindAsync(id, ct);
        if (branch is null) return OperationResult<BranchDto>.NotFound("La sede no existe.");

        if (active)
        {
            branch.Activate();
        }
        else
        {
            var result = branch.Deactivate(await members.CountByBranchAsync(id, ct));
            if (result.IsFailure) return OperationResult<BranchDto>.From(result.Error!);
        }

        audit.Record(active ? "branch.activated" : "branch.deactivated", $"{(active ? "Activó" : "Desactivó")} la sede {branch.Name}.",
            "Organización", branch.Id.ToString());
        await unitOfWork.SaveChangesAsync(ct);
        return await BranchDtoAsync(branch, ct);
    }

    private async Task<OperationResult> ValidateBranchAsync(SaveBranchCommand command, Guid? exceptId, CancellationToken ct)
    {
        if (await branches.NameExistsAsync(Text.Key(command.Name), exceptId, ct))
            return OperationResult.Invalid("Ya existe una sede con ese nombre.", "name", "branch.name_taken");

        if (string.IsNullOrWhiteSpace(command.TimeZoneId) || !TimeZoneInfo.TryFindSystemTimeZoneById(command.TimeZoneId, out _))
            return OperationResult.Invalid("La zona horaria de la ciudad no es válida.", "timeZoneId", "branch.time_zone");

        if (command.MainSiteId is { } siteId && await sites.FindAsync(siteId, ct) is not { IsActive: true })
            return OperationResult.Invalid("Elige un sitio activo.", "mainSiteId");

        if (command.DefaultTemplateId is { } templateId && await templates.FindAsync(templateId, ct) is not { IsActive: true })
            return OperationResult.Invalid("Elige un horario activo.", "defaultTemplateId");

        return OperationResult.Ok();
    }

    private async Task<BranchDto> BranchDtoAsync(Branch branch, CancellationToken ct)
    {
        var site = branch.MainSiteId is { } s ? await sites.FindAsync(s, ct) : null;
        var template = branch.DefaultTemplateId is { } t ? await templates.FindAsync(t, ct) : null;
        var groupCount = (await groups.ListAsync(branch.Id, ct)).Count(g => g.IsActive);
        return ToDto(branch,
            site is null ? new Dictionary<Guid, string>() : new Dictionary<Guid, string> { [site.Id] = site.Name },
            template is null ? new Dictionary<Guid, string>() : new Dictionary<Guid, string> { [template.Id] = template.Name },
            groupCount,
            await members.CountByBranchAsync(branch.Id, ct));
    }

    private static BranchData ToData(SaveBranchCommand c) =>
        new(c.Name ?? string.Empty, c.CountryCode ?? string.Empty, c.City ?? string.Empty, c.Region, c.TimeZoneId ?? string.Empty);

    private static BranchDto ToDto(Branch b, IReadOnlyDictionary<Guid, string> sites, IReadOnlyDictionary<Guid, string> templates, int groupCount, int people) =>
        new(b.Id, b.Name, b.CountryCode, b.City, b.Region, b.TimeZoneId,
            b.MainSiteId, b.MainSiteId is { } s ? sites.GetValueOrDefault(s) : null,
            b.DefaultTemplateId, b.DefaultTemplateId is { } t ? templates.GetValueOrDefault(t) : null,
            b.Status, groupCount, people);

    // ── Grupos ──────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<WorkGroupDto>> ListGroupsAsync(Guid? branchId, CancellationToken ct = default)
    {
        var list = await groups.ListAsync(branchId, ct);
        var branchNames = (await branches.ListAsync(ct)).ToDictionary(b => b.Id, b => b.Name);
        var people = await members.ListAllAsync(ct);
        var names = people.ToDictionary(m => m.Id, m => m.FullName);
        var templateNames = (await templates.ListAsync(ct)).ToDictionary(t => t.Id, t => t.Name);

        return list
            .OrderBy(g => !g.IsActive)
            .ThenBy(g => g.Name)
            .Select(g => ToDto(g, branchNames, names, templateNames,
                people.Count(m => m.WorkGroupId == g.Id && m.Status != Domain.Access.MemberStatus.Inactive)))
            .ToList();
    }

    public async Task<OperationResult<WorkGroupDto>> CreateGroupAsync(SaveWorkGroupCommand command, CancellationToken ct = default)
    {
        var check = await ValidateGroupAsync(command, null, ct);
        if (!check.Succeeded) return OperationResult<WorkGroupDto>.From(check);

        var created = WorkGroup.Create(actor.TenantId, ToData(command));
        if (created.IsFailure) return OperationResult<WorkGroupDto>.From(created.Error!);

        groups.Add(created.Value);
        audit.Record("group.created", $"Creó el grupo {created.Value.Name}.", "Organización", created.Value.Id.ToString());
        await unitOfWork.SaveChangesAsync(ct);
        return await GroupDtoAsync(created.Value, ct);
    }

    public async Task<OperationResult<WorkGroupDto>> UpdateGroupAsync(Guid id, SaveWorkGroupCommand command, CancellationToken ct = default)
    {
        var group = await groups.FindAsync(id, ct);
        if (group is null) return OperationResult<WorkGroupDto>.NotFound("El grupo no existe.");

        var check = await ValidateGroupAsync(command, id, ct);
        if (!check.Succeeded) return OperationResult<WorkGroupDto>.From(check);

        if (group.BranchId != command.BranchId && await members.CountByGroupAsync(id, ct) > 0)
            return OperationResult<WorkGroupDto>.Invalid("El grupo tiene personas: muévelas antes de cambiarlo de sede.", "branchId");

        var updated = group.Update(ToData(command));
        if (updated.IsFailure) return OperationResult<WorkGroupDto>.From(updated.Error!);

        audit.Record("group.updated", $"Actualizó el grupo {group.Name}.", "Organización", group.Id.ToString());
        await unitOfWork.SaveChangesAsync(ct);
        return await GroupDtoAsync(group, ct);
    }

    public async Task<OperationResult<WorkGroupDto>> SetGroupActiveAsync(Guid id, bool active, CancellationToken ct = default)
    {
        var group = await groups.FindAsync(id, ct);
        if (group is null) return OperationResult<WorkGroupDto>.NotFound("El grupo no existe.");

        if (active)
        {
            group.Activate();
        }
        else
        {
            var result = group.Deactivate(await members.CountByGroupAsync(id, ct));
            if (result.IsFailure) return OperationResult<WorkGroupDto>.From(result.Error!);
        }

        audit.Record(active ? "group.activated" : "group.deactivated", $"{(active ? "Activó" : "Desactivó")} el grupo {group.Name}.",
            "Organización", group.Id.ToString());
        await unitOfWork.SaveChangesAsync(ct);
        return await GroupDtoAsync(group, ct);
    }

    private async Task<OperationResult> ValidateGroupAsync(SaveWorkGroupCommand command, Guid? exceptId, CancellationToken ct)
    {
        if (await branches.FindAsync(command.BranchId, ct) is not { IsActive: true })
            return OperationResult.Invalid("Elige una sede activa.", "branchId");

        if (await groups.NameExistsAsync(Text.Key(command.Name), command.BranchId, exceptId, ct))
            return OperationResult.Invalid("Ya existe un grupo con ese nombre en la sede.", "name", "group.name_taken");

        if (command.SupervisorId is { } supervisorId && await members.FindAsync(supervisorId, ct) is not { IsActive: true })
            return OperationResult.Invalid("Elige un supervisor activo.", "supervisorId");

        if (command.DefaultTemplateId is { } templateId && await templates.FindAsync(templateId, ct) is not { IsActive: true })
            return OperationResult.Invalid("Elige un horario activo.", "defaultTemplateId");

        return OperationResult.Ok();
    }

    private async Task<WorkGroupDto> GroupDtoAsync(WorkGroup group, CancellationToken ct)
    {
        var branch = await branches.FindAsync(group.BranchId, ct);
        var supervisor = group.SupervisorId is { } s ? await members.FindAsync(s, ct) : null;
        var template = group.DefaultTemplateId is { } t ? await templates.FindAsync(t, ct) : null;

        return ToDto(group,
            branch is null ? new Dictionary<Guid, string>() : new Dictionary<Guid, string> { [branch.Id] = branch.Name },
            supervisor is null ? new Dictionary<Guid, string>() : new Dictionary<Guid, string> { [supervisor.Id] = supervisor.FullName },
            template is null ? new Dictionary<Guid, string>() : new Dictionary<Guid, string> { [template.Id] = template.Name },
            await members.CountByGroupAsync(group.Id, ct));
    }

    private static WorkGroupData ToData(SaveWorkGroupCommand c) =>
        new(c.Name ?? string.Empty, c.BranchId, c.SupervisorId, c.DefaultTemplateId, c.MdmGroupPath);

    private static WorkGroupDto ToDto(WorkGroup g, IReadOnlyDictionary<Guid, string> branchNames, IReadOnlyDictionary<Guid, string> people,
        IReadOnlyDictionary<Guid, string> templateNames, int count) =>
        new(g.Id, g.Name, g.BranchId, branchNames.GetValueOrDefault(g.BranchId, string.Empty),
            g.SupervisorId, g.SupervisorId is { } s ? people.GetValueOrDefault(s) : null,
            g.DefaultTemplateId, g.DefaultTemplateId is { } t ? templateNames.GetValueOrDefault(t) : null,
            g.MdmGroupPath, g.IsActive, count);

    // ── Sitios ──────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<WorkSiteDto>> ListSitesAsync(bool includeInactive, CancellationToken ct = default)
    {
        var list = await sites.ListAsync(includeInactive, ct);
        var counts = await WeekWorkOrdersAsync(ct);
        return list.OrderBy(s => !s.IsActive).ThenBy(s => s.Name).Select(s => ToDto(s, counts.GetValueOrDefault(s.Id))).ToList();
    }

    public async Task<OperationResult<WorkSiteDto>> CreateSiteAsync(SaveWorkSiteCommand command, CancellationToken ct = default)
    {
        if (await sites.NameExistsAsync(Text.Key(command.Name), null, ct))
            return OperationResult<WorkSiteDto>.Invalid("Ya existe un sitio con ese nombre.", "name", "site.name_taken");

        var geofence = await BuildGeofenceAsync(command, ct);
        if (geofence.IsFailure) return OperationResult<WorkSiteDto>.From(geofence.Error!);

        var created = WorkSite.Create(actor.TenantId, command.Name ?? string.Empty, command.Address, geofence.Value);
        if (created.IsFailure) return OperationResult<WorkSiteDto>.From(created.Error!);

        sites.Add(created.Value);
        audit.Record("site.created", $"Creó el sitio {created.Value.Name} ({DescribeGeofence(created.Value)}).", "Sitios", created.Value.Id.ToString());
        await unitOfWork.SaveChangesAsync(ct);

        // El sitio ya quedó; la consola se intenta después y un fallo solo lo deja pendiente.
        await geofences.SyncAsync(created.Value, shapeChanged: false, ct);
        await unitOfWork.SaveChangesAsync(ct);
        return ToDto(created.Value, 0);
    }

    public async Task<OperationResult<WorkSiteDto>> UpdateSiteAsync(Guid id, SaveWorkSiteCommand command, CancellationToken ct = default)
    {
        var site = await sites.FindAsync(id, ct);
        if (site is null) return OperationResult<WorkSiteDto>.NotFound("El sitio no existe.");

        if (await sites.NameExistsAsync(Text.Key(command.Name), id, ct))
            return OperationResult<WorkSiteDto>.Invalid("Ya existe un sitio con ese nombre.", "name", "site.name_taken");

        var geofence = await BuildGeofenceAsync(command, ct);
        if (geofence.IsFailure) return OperationResult<WorkSiteDto>.From(geofence.Error!);

        var before = DescribeGeofence(site);
        var shapeBefore = (site.Shape, site.CenterLatitude, site.CenterLongitude, site.RadiusMeters, site.PolygonVertices);
        var updated = site.Update(command.Name ?? string.Empty, command.Address, geofence.Value);
        if (updated.IsFailure) return OperationResult<WorkSiteDto>.From(updated.Error!);

        audit.Record("site.updated", $"Actualizó el sitio {site.Name}.", "Sitios", site.Id.ToString(),
            new { Geofence = before }, new { Geofence = DescribeGeofence(site) });
        await unitOfWork.SaveChangesAsync(ct);

        var shapeChanged = shapeBefore != (site.Shape, site.CenterLatitude, site.CenterLongitude, site.RadiusMeters, site.PolygonVertices);
        await geofences.SyncAsync(site, shapeChanged, ct);
        await unitOfWork.SaveChangesAsync(ct);
        var counts = await WeekWorkOrdersAsync(ct);
        return ToDto(site, counts.GetValueOrDefault(site.Id));
    }

    public async Task<OperationResult<WorkSiteDto>> SetSiteActiveAsync(Guid id, bool active, CancellationToken ct = default)
    {
        var site = await sites.FindAsync(id, ct);
        if (site is null) return OperationResult<WorkSiteDto>.NotFound("El sitio no existe.");

        if (active) site.Activate();
        else site.Deactivate();

        audit.Record(active ? "site.activated" : "site.deactivated", $"{(active ? "Activó" : "Desactivó")} el sitio {site.Name}.",
            "Sitios", site.Id.ToString());
        await unitOfWork.SaveChangesAsync(ct);

        // Desactivar lo borra de MobiControl; activar lo vuelve a crear.
        await geofences.SyncAsync(site, shapeChanged: false, ct);
        await unitOfWork.SaveChangesAsync(ct);
        return ToDto(site, 0);
    }

    public async Task<OperationResult<IReadOnlyList<WorkSiteDto>>> SyncSitesToMobiControlAsync(Guid? id, CancellationToken ct = default)
    {
        var targets = id is { } siteId
            ? (await sites.FindAsync(siteId, ct) is { } one ? [one] : [])
            : (await sites.ListAsync(true, ct)).Where(s => s.IsActive || s.MobiControlName is not null).ToList();
        if (id is not null && targets.Count == 0) return OperationResult<IReadOnlyList<WorkSiteDto>>.NotFound("El sitio no existe.");

        foreach (var site in targets) await geofences.SyncAsync(site, shapeChanged: false, ct);

        var pending = targets.Count(s => s.MobiControlError is not null);
        audit.Record("site.mobicontrol_synced",
            $"Sincronizó {targets.Count} sitio(s) con MobiControl{(pending > 0 ? $" ({pending} pendiente(s))" : "")}.", "Sitios", id?.ToString());
        await unitOfWork.SaveChangesAsync(ct);
        return targets.Select(s => ToDto(s, 0)).ToList();
    }

    private async Task<Result<Geofence>> BuildGeofenceAsync(SaveWorkSiteCommand command, CancellationToken ct)
    {
        if (command.Shape == GeofenceShape.Polygon)
        {
            var vertices = new List<GeoPoint>();
            foreach (var vertex in command.Vertices ?? [])
            {
                var point = GeoPoint.Create(vertex.Latitude, vertex.Longitude);
                if (point.IsFailure) return point.Error!;
                vertices.Add(point.Value);
            }

            return Geofence.Polygon(vertices);
        }

        var center = GeoPoint.Create(command.Latitude, command.Longitude);
        if (center.IsFailure) return center.Error!;

        var radius = command.RadiusMeters ?? (await parameters.ForCompanyAsync(LocalTime.Today(clock, LocalTime.Find(null)), ct))
            [Domain.Parameters.ParameterKey.DefaultGeofenceRadiusMeters];
        return Geofence.Circle(center.Value, radius);
    }

    private async Task<IReadOnlyDictionary<Guid, int>> WeekWorkOrdersAsync(CancellationToken ct)
    {
        var today = LocalTime.Today(clock, LocalTime.Find(null));
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        return await workOrders.CountPerSiteAsync(monday, monday.AddDays(6), ct);
    }

    private static string DescribeGeofence(WorkSite site) =>
        site.Shape == GeofenceShape.Circle ? $"círculo de {site.RadiusMeters} m" : $"polígono de {site.Geofence.Vertices.Count} esquinas";

    private static WorkSiteDto ToDto(WorkSite s, int workOrders) =>
        new(s.Id, s.Name, s.Address, s.Shape, s.CenterLatitude, s.CenterLongitude, s.RadiusMeters,
            s.Geofence.Vertices.Select(v => new GeoPointDto(v.Latitude, v.Longitude)).ToList(),
            s.IsActive, workOrders, s.MobiControlName, s.MobiControlSyncedAt, s.MobiControlError);
}
