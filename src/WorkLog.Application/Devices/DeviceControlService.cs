using WorkLog.Application.Abstractions;
using WorkLog.Application.Common;
using WorkLog.Domain.Access;
using WorkLog.Domain.Devices;

namespace WorkLog.Application.Devices;

public sealed record DeviceControlSettingsDto(
    bool Enabled,
    IReadOnlyList<Guid> BranchIds,
    IReadOnlyList<Guid> GroupIds,
    IReadOnlyList<int> WarningMinutes,
    bool LogoutSharedOnClose,
    OfflinePolicy OfflinePolicy,
    IReadOnlyList<string> CorporateApps,
    IReadOnlyList<string> AlwaysAvailableApps,
    IReadOnlyList<string> FixedApps,
    DateTimeOffset? LastVerifiedAt,
    bool LastVerificationPassed);

public sealed record SaveDeviceControlCommand(
    bool Enabled,
    IReadOnlyList<Guid>? BranchIds,
    IReadOnlyList<Guid>? GroupIds,
    IReadOnlyList<int>? WarningMinutes,
    bool LogoutSharedOnClose,
    OfflinePolicy OfflinePolicy,
    IReadOnlyList<string>? CorporateApps,
    IReadOnlyList<string>? AlwaysAvailableApps);

public sealed record VerificationDto(bool Passed, DateTimeOffset VerifiedAt, IReadOnlyList<VerificationCheck> Checks);

public sealed record DeviceRowDto(
    string DeviceId,
    string Model,
    string Serial,
    string GroupPath,
    bool IsShared,
    bool Online,
    int? BatteryPercent,
    DateTimeOffset? LastSeenAt,
    DateTimeOffset EnrolledAt,
    Guid? MemberId,
    string? MemberName,
    bool WorkdayOpen,
    DateTimeOffset? WorkdaySince,
    DeviceCommandType? LastCommand,
    DeviceCommandStatus? LastCommandStatus,
    DateTimeOffset? LastCommandAt);

public sealed record AssignDeviceCommand(string? DeviceId);

public interface IDeviceControlService
{
    Task<DeviceControlSettingsDto> GetSettingsAsync(CancellationToken ct = default);
    Task<OperationResult<DeviceControlSettingsDto>> SaveSettingsAsync(SaveDeviceControlCommand command, CancellationToken ct = default);
    Task<VerificationDto> VerifyAsync(CancellationToken ct = default);
    Task<IReadOnlyList<DeviceRowDto>> ListDevicesAsync(bool unassignedOnly, CancellationToken ct = default);
    Task<IReadOnlyList<string>> ListAppsAsync(CancellationToken ct = default);
    Task<OperationResult> AssignAsync(Guid memberId, AssignDeviceCommand command, CancellationToken ct = default);
}

/// <summary>
/// Orquesta las órdenes a MobiControl cuando abre o cierra una jornada. No es un puerto de
/// entrada: lo usan los casos de uso de la jornada y el cierre automático.
/// </summary>
public interface IDeviceOrchestrator
{
    /// <summary>Si devuelve false, MobiControl no respondió y la política pide bloquear el inicio.</summary>
    Task<bool> OnWorkdayOpeningAsync(Member member, CancellationToken ct = default);

    Task OnWorkdayClosedAsync(Member member, CancellationToken ct = default);

    /// <summary>Estado de los equipos indexado por id (batería, en línea), para Equipo hoy.</summary>
    Task<IReadOnlyDictionary<string, ManagedDevice>> DevicesAsync(CancellationToken ct = default);

    Task<DeviceControlSettings> SettingsAsync(CancellationToken ct = default);
}

internal sealed class DeviceOrchestrator(
    ITenantContext tenant,
    IDeviceControlRepository settingsRepository,
    IDeviceCommandRepository commands,
    IDeviceManagementGateway gateway,
    IAuditTrail audit,
    TimeProvider clock) : IDeviceOrchestrator
{
    private DeviceControlSettings? _settings;

    public async Task<DeviceControlSettings> SettingsAsync(CancellationToken ct = default) =>
        _settings ??= await settingsRepository.GetAsync(ct) ?? DeviceControlSettings.Default(tenant.TenantId ?? Guid.Empty);

    public async Task<bool> OnWorkdayOpeningAsync(Member member, CancellationToken ct = default)
    {
        var settings = await SettingsAsync(ct);
        if (member.DeviceId is not { } device || !settings.AppliesTo(member.BranchId, member.WorkGroupId)) return true;

        var result = await gateway.SetWorkdayStateAsync(device, open: true, ct);
        commands.Add(DeviceCommand.Record(member.TenantId, device, member.Id, DeviceCommandType.OpenWorkday, result.Status, clock.GetUtcNow(), result.Error));

        if (result.Reached) return true;

        audit.RecordSystem(member.TenantId, "device.unreachable",
            $"MobiControl no respondió al iniciar la jornada de {member.FullName}: {result.Error}.", "Dispositivos", device);
        return settings.OfflinePolicy == OfflinePolicy.AllowAndNotify;
    }

    public async Task OnWorkdayClosedAsync(Member member, CancellationToken ct = default)
    {
        var settings = await SettingsAsync(ct);
        if (member.DeviceId is not { } device || !settings.AppliesTo(member.BranchId, member.WorkGroupId)) return;

        var now = clock.GetUtcNow();
        var closed = await gateway.SetWorkdayStateAsync(device, open: false, ct);
        commands.Add(DeviceCommand.Record(member.TenantId, device, member.Id, DeviceCommandType.CloseWorkday, closed.Status, now, closed.Error));

        if (settings.LogoutSharedOnClose && (await DevicesAsync(ct)).GetValueOrDefault(device) is { IsShared: true })
        {
            var logout = await gateway.LogoutSharedAsync(device, ct);
            commands.Add(DeviceCommand.Record(member.TenantId, device, member.Id, DeviceCommandType.LogoutShared, logout.Status, now, logout.Error));
        }

        audit.RecordSystem(member.TenantId, "device.workday_closed",
            $"Pidió a MobiControl el perfil «Fuera de jornada» para el equipo de {member.FullName} ({closed.Status}).", "Dispositivos", device);
    }

    public async Task<IReadOnlyDictionary<string, ManagedDevice>> DevicesAsync(CancellationToken ct = default) =>
        (await gateway.ListDevicesAsync(ct)).ToDictionary(d => d.DeviceId);
}

internal sealed class DeviceControlService(
    ICurrentActor actor,
    IDeviceControlRepository settingsRepository,
    IDeviceCommandRepository commands,
    IDeviceManagementGateway gateway,
    IMemberRepository members,
    IOpenWorkdayQueries openWorkdays,
    IAuditTrail audit,
    IUnitOfWork unitOfWork,
    TimeProvider clock) : IDeviceControlService
{
    public async Task<DeviceControlSettingsDto> GetSettingsAsync(CancellationToken ct = default) =>
        ToDto(await settingsRepository.GetAsync(ct) ?? DeviceControlSettings.Default(actor.TenantId));

    public async Task<OperationResult<DeviceControlSettingsDto>> SaveSettingsAsync(SaveDeviceControlCommand command, CancellationToken ct = default)
    {
        var settings = await EnsureSettingsAsync(ct);
        var wasEnabled = settings.Enabled;
        var updated = settings.Update(new DeviceControlData(command.Enabled, command.BranchIds ?? [], command.GroupIds ?? [],
            command.WarningMinutes ?? [], command.LogoutSharedOnClose, command.OfflinePolicy, command.CorporateApps ?? [],
            command.AlwaysAvailableApps ?? []));
        if (updated.IsFailure) return OperationResult<DeviceControlSettingsDto>.From(updated.Error!);

        audit.Record("device_control.updated",
            settings.Enabled
                ? $"{(wasEnabled ? "Actualizó" : "Activó")} «Apps corporativas solo en jornada» ({settings.BranchIds.Count + settings.GroupIds.Count} alcance(s))."
                : "La opción «Apps corporativas solo en jornada» quedó apagada.",
            "Dispositivos", null, after: new { settings.Enabled, settings.WarningMinutes, settings.OfflinePolicy });
        await unitOfWork.SaveChangesAsync(ct);
        return ToDto(settings);
    }

    public async Task<VerificationDto> VerifyAsync(CancellationToken ct = default)
    {
        var checks = await gateway.VerifyAsync(ct);
        var passed = checks.All(c => c.Passed);
        var now = clock.GetUtcNow();

        var settings = await EnsureSettingsAsync(ct);
        settings.RecordVerification(passed, now);
        audit.Record("device_control.verified",
            passed ? "Verificó MobiControl: todo listo." : $"Verificó MobiControl: {checks.Count(c => !c.Passed)} punto(s) pendiente(s).",
            "Dispositivos", null);
        await unitOfWork.SaveChangesAsync(ct);
        return new VerificationDto(passed, now, checks);
    }

    public async Task<IReadOnlyList<DeviceRowDto>> ListDevicesAsync(bool unassignedOnly, CancellationToken ct = default)
    {
        var devices = await gateway.ListDevicesAsync(ct);
        var people = (await members.ListAllAsync(ct))
            .Where(m => m.DeviceId is not null)
            .GroupBy(m => m.DeviceId!)
            .ToDictionary(g => g.Key, g => g.First());
        var open = (await openWorkdays.ListOpenAsync(ct)).GroupBy(w => w.MemberId).ToDictionary(g => g.Key, g => g.First());
        var latest = await commands.LatestByDeviceAsync(ct);

        return devices
            .Where(d => !unassignedOnly || !people.ContainsKey(d.DeviceId))
            .Select(d =>
            {
                var person = people.GetValueOrDefault(d.DeviceId);
                var workday = person is null ? null : open.GetValueOrDefault(person.Id);
                var command = latest.GetValueOrDefault(d.DeviceId);
                return new DeviceRowDto(d.DeviceId, d.Model, d.Serial, d.GroupPath, d.IsShared, d.Online, d.BatteryPercent, d.LastSeenAt,
                    d.EnrolledAt, person?.Id, person?.FullName, workday is not null, workday?.StartedAt,
                    command?.Type, command?.Status, command?.RequestedAt);
            })
            .OrderBy(d => d.MemberName is null)
            .ThenBy(d => d.MemberName)
            .ThenBy(d => d.Serial)
            .ToList();
    }

    public Task<IReadOnlyList<string>> ListAppsAsync(CancellationToken ct = default) => gateway.ListAppsAsync(ct);

    public async Task<OperationResult> AssignAsync(Guid memberId, AssignDeviceCommand command, CancellationToken ct = default)
    {
        var member = await members.FindAsync(memberId, ct);
        if (member is null) return OperationResult.NotFound("La persona no existe.");

        var device = (await gateway.ListDevicesAsync(ct)).FirstOrDefault(d => d.DeviceId == command.DeviceId);
        if (device is null) return OperationResult.Invalid("El dispositivo no está inscrito en MobiControl.", "deviceId");

        // Un equipo asignado es de una sola persona; uno compartido (Shared Device) lo usan varias.
        var holder = (await members.ListAllAsync(ct)).FirstOrDefault(m => m.DeviceId == device.DeviceId && m.Id != memberId);
        if (holder is not null && !device.IsShared)
            return OperationResult.Conflict($"El dispositivo ya está asignado a {holder.FullName}.", "device.taken");

        var assigned = member.AssignDevice(device.DeviceId, device.Model);
        if (assigned.IsFailure) return OperationResult.From(assigned.Error!);

        audit.Record("member.device_assigned", $"Asignó el dispositivo {device.Serial} a {member.FullName}.", "Dispositivos", member.Id.ToString());
        await unitOfWork.SaveChangesAsync(ct);
        return OperationResult.Ok();
    }

    private async Task<DeviceControlSettings> EnsureSettingsAsync(CancellationToken ct)
    {
        var settings = await settingsRepository.GetAsync(ct);
        if (settings is not null) return settings;

        settings = DeviceControlSettings.Default(actor.TenantId);
        settingsRepository.Add(settings);
        return settings;
    }

    private static DeviceControlSettingsDto ToDto(DeviceControlSettings s) => new(
        s.Enabled, s.BranchIds, s.GroupIds, s.WarningMinutes, s.LogoutSharedOnClose, s.OfflinePolicy, s.CorporateApps,
        s.AlwaysAvailableApps, DeviceControlSettings.FixedApps, s.LastVerifiedAt, s.LastVerificationPassed);
}
