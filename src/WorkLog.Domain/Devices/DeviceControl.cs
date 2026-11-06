using WorkLog.Domain.Common;

namespace WorkLog.Domain.Devices;

/// <summary>Qué hacer si MobiControl no responde al iniciar la jornada.</summary>
public enum OfflinePolicy
{
    /// <summary>Dejar iniciar y avisar al administrador.</summary>
    AllowAndNotify = 1,

    /// <summary>No dejar iniciar hasta que MobiControl responda.</summary>
    Block = 2
}

public sealed record DeviceControlData(
    bool Enabled,
    IReadOnlyList<Guid> BranchIds,
    IReadOnlyList<Guid> GroupIds,
    IReadOnlyList<int> WarningMinutes,
    bool LogoutSharedOnClose,
    OfflinePolicy OfflinePolicy,
    IReadOnlyList<string> CorporateApps,
    IReadOnlyList<string> AlwaysAvailableApps);

/// <summary>
/// "Apps corporativas solo en jornada": al iniciar la jornada WorkLog pide a MobiControl el perfil
/// «En jornada»; al cerrar o terminar el turno, vuelve a «Fuera de jornada» (kiosco con WorkLog y
/// Teléfono). La ventana de inicio, la gracia y los avisos salen de los parámetros de la sede.
/// </summary>
public sealed class DeviceControlSettings : TenantEntity
{
    public static readonly IReadOnlyList<int> AllowedWarnings = [30, 15, 10, 5];

    /// <summary>Siempre disponibles, no se pueden mover: la app misma y el teléfono para emergencias.</summary>
    public static readonly IReadOnlyList<string> FixedApps = ["com.intechsys.worklog", "com.android.dialer"];

    private DeviceControlSettings()
    {
    }

    private DeviceControlSettings(Guid tenantId) : base(tenantId)
    {
    }

    public bool Enabled { get; private set; }
    public string BranchScope { get; private set; } = string.Empty;
    public string GroupScope { get; private set; } = string.Empty;
    public string Warnings { get; private set; } = "15,5";
    public bool LogoutSharedOnClose { get; private set; } = true;
    public OfflinePolicy OfflinePolicy { get; private set; } = OfflinePolicy.AllowAndNotify;
    public string CorporateAppList { get; private set; } = string.Empty;
    public string AlwaysAvailableAppList { get; private set; } = string.Empty;
    public DateTimeOffset? LastVerifiedAt { get; private set; }
    public bool LastVerificationPassed { get; private set; }

    public IReadOnlyList<Guid> BranchIds => ParseGuids(BranchScope);
    public IReadOnlyList<Guid> GroupIds => ParseGuids(GroupScope);
    public IReadOnlyList<int> WarningMinutes => Warnings.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToList();
    public IReadOnlyList<string> CorporateApps => Lines(CorporateAppList);
    public IReadOnlyList<string> AlwaysAvailableApps => FixedApps.Concat(Lines(AlwaysAvailableAppList)).Distinct().ToList();

    public static DeviceControlSettings Default(Guid tenantId) => new(tenantId);

    public void RecordVerification(bool passed, DateTimeOffset at)
    {
        LastVerificationPassed = passed;
        LastVerifiedAt = at;
        if (!passed) Enabled = false;
    }

    public Result Update(DeviceControlData data)
    {
        var warnings = data.WarningMinutes.Distinct().OrderByDescending(w => w).ToList();
        if (warnings.Any(w => !AllowedWarnings.Contains(w)))
            return DomainError.Validation("device.warnings", "Los avisos permitidos son 30, 15, 10 y 5 minutos.", "warningMinutes");

        if (data.Enabled)
        {
            if (!LastVerificationPassed)
                return DomainError.Validation("device.not_verified", "Verifica la conexión con MobiControl y corrige lo pendiente antes de activar.");

            if (data.BranchIds.Count + data.GroupIds.Count == 0)
                return DomainError.Validation("device.scope", "Elige al menos una sede o grupo.", "branchIds");

            if (warnings.Count == 0)
                return DomainError.Validation("device.warning_required", "Elige al menos un aviso antes del cierre.", "warningMinutes");
        }

        var corporate = CleanApps(data.CorporateApps).Where(a => !FixedApps.Contains(a)).ToList();
        var always = CleanApps(data.AlwaysAvailableApps).Where(a => !FixedApps.Contains(a) && !corporate.Contains(a)).ToList();

        Enabled = data.Enabled;
        BranchScope = string.Join(',', data.BranchIds.Distinct());
        GroupScope = string.Join(',', data.GroupIds.Distinct());
        Warnings = string.Join(',', warnings);
        LogoutSharedOnClose = data.LogoutSharedOnClose;
        OfflinePolicy = data.OfflinePolicy;
        CorporateAppList = string.Join('\n', corporate);
        AlwaysAvailableAppList = string.Join('\n', always);
        return Result.Success();
    }

    /// <summary>Aplica a la persona si su sede o su grupo están en el alcance.</summary>
    public bool AppliesTo(Guid? branchId, Guid? groupId) =>
        Enabled && ((branchId is { } b && BranchIds.Contains(b)) || (groupId is { } g && GroupIds.Contains(g)));

    private static IReadOnlyList<Guid> ParseGuids(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(Guid.Parse).ToList();

    private static IReadOnlyList<string> Lines(string value) => value.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private static IEnumerable<string> CleanApps(IEnumerable<string> apps) =>
        apps.Select(a => Text.Clean(a).ToLowerInvariant()).Where(a => a.Length is > 2 and <= 150).Distinct();
}

public enum DeviceCommandType
{
    /// <summary>Perfil «En jornada» (atributo WL_JORNADA = ABIERTA).</summary>
    OpenWorkday = 1,

    /// <summary>Perfil «Fuera de jornada» (WL_JORNADA = CERRADA).</summary>
    CloseWorkday = 2,

    /// <summary>Cerrar la sesión de Shared Device.</summary>
    LogoutShared = 3
}

public enum DeviceCommandStatus
{
    Applied = 1,

    /// <summary>MobiControl la recibió; el equipo la aplica cuando se conecte.</summary>
    Queued = 2,

    Failed = 3
}

/// <summary>Orden enviada a MobiControl. Queda como traza de lo que se pidió a cada equipo.</summary>
public sealed class DeviceCommand : Entity, ITenantOwned
{
    private DeviceCommand()
    {
    }

    public Guid TenantId { get; private set; }
    public string DeviceId { get; private set; } = string.Empty;
    public Guid MemberId { get; private set; }
    public DeviceCommandType Type { get; private set; }
    public DeviceCommandStatus Status { get; private set; }
    public DateTimeOffset RequestedAt { get; private set; }
    public string? Error { get; private set; }

    public static DeviceCommand Record(Guid tenantId, string deviceId, Guid memberId, DeviceCommandType type,
        DeviceCommandStatus status, DateTimeOffset at, string? error) => new()
    {
        TenantId = tenantId,
        DeviceId = deviceId,
        MemberId = memberId,
        Type = type,
        Status = status,
        RequestedAt = at,
        Error = error is { Length: > 400 } ? error[..400] : error
    };
}
