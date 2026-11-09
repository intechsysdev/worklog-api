using WorkLog.Application.Common;
using WorkLog.Domain.Absences;
using WorkLog.Domain.Auditing;
using WorkLog.Domain.Devices;
using WorkLog.Domain.Organization;
using WorkLog.Domain.Payroll;
using WorkLog.Domain.Requests;
using WorkLog.Domain.Workdays;

namespace WorkLog.Application.Abstractions;

public sealed record RequestFilter(
    PageRequest Page,
    IReadOnlyCollection<Guid>? MemberIds = null,
    RequestType? Type = null,
    RequestStatus? Status = null,
    DateOnly? From = null,
    DateOnly? To = null);

public interface IWorkRequestRepository : IRepository<WorkRequest>
{
    Task<PagedResult<WorkRequest>> SearchAsync(RequestFilter filter, CancellationToken ct = default);

    Task<IReadOnlyList<WorkRequest>> ListAsync(IReadOnlyCollection<Guid> memberIds, DateOnly from, DateOnly to, RequestStatus? status, CancellationToken ct = default);

    Task<int> CountPendingAsync(CancellationToken ct = default);
}

public sealed record AbsenceFilter(
    PageRequest Page,
    AbsenceType? Type = null,
    AbsenceStatus? Status = null,
    Guid? MemberId = null,
    DateOnly? From = null,
    DateOnly? To = null);

public interface IAbsenceQueries
{
    Task<PagedResult<Absence>> SearchAsync(AbsenceFilter filter, CancellationToken ct = default);

    /// <summary>Novedades en cualquier estado que tocan el rango (para el cierre y el panel).</summary>
    Task<IReadOnlyList<Absence>> ListAsync(IReadOnlyCollection<Guid> memberIds, DateOnly from, DateOnly to, CancellationToken ct = default);
}

public interface IPayrollPeriodRepository
{
    Task<PayrollPeriod?> FindAsync(Guid branchId, DateOnly from, CancellationToken ct = default);

    Task<IReadOnlyList<PayrollPeriod>> ListAsync(Guid branchId, CancellationToken ct = default);

    /// <summary>¿La fecha cae en un periodo cerrado de esa sede?</summary>
    Task<bool> IsClosedAsync(Guid branchId, DateOnly date, CancellationToken ct = default);

    void Add(PayrollPeriod period);
}

public sealed record AuditFilter(
    PageRequest Page,
    AuditOrigin? Origin = null,
    string? EntityType = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null);

/// <summary>Lectura de la auditoría (la escritura va por <see cref="IAuditStore"/>).</summary>
public interface IAuditQueries
{
    Task<PagedResult<AuditEvent>> SearchAsync(AuditFilter filter, CancellationToken ct = default);

    Task<AuditEvent?> FindAsync(Guid id, CancellationToken ct = default);

    Task<IReadOnlyList<string>> EntityTypesAsync(CancellationToken ct = default);
}

public interface IDeviceControlRepository
{
    Task<DeviceControlSettings?> GetAsync(CancellationToken ct = default);

    void Add(DeviceControlSettings settings);
}

public interface IDeviceCommandRepository
{
    void Add(DeviceCommand command);

    /// <summary>Última orden de cada equipo.</summary>
    Task<IReadOnlyDictionary<string, DeviceCommand>> LatestByDeviceAsync(CancellationToken ct = default);
}

public interface IOpenWorkdayQueries
{
    /// <summary>Jornadas abiertas de la empresa (para el cierre automático), con sus actividades.</summary>
    Task<IReadOnlyList<Workday>> ListOpenAsync(CancellationToken ct = default);
}

public interface ICompanyDirectory
{
    /// <summary>Todas las empresas registradas (los procesos automáticos recorren cada una).</summary>
    Task<IReadOnlyList<Company>> ListAsync(CancellationToken ct = default);
}

/// <summary>Equipo inscrito en MobiControl, tal como lo reporta la consola.</summary>
public sealed record ManagedDevice(
    string DeviceId,
    string Model,
    string Serial,
    string GroupPath,
    bool IsShared,
    bool Online,
    int? BatteryPercent,
    DateTimeOffset? LastSeenAt,
    DateTimeOffset? EnrolledAt);

public sealed record VerificationCheck(string Code, string Name, bool Passed, string Detail);

public sealed record GatewayResult(DeviceCommandStatus Status, string? Error)
{
    public bool Reached => Status != DeviceCommandStatus.Failed;
}

/// <summary>
/// Puerto hacia SOTI MobiControl. Hoy lo implementa un simulador; el adaptador real usará la API
/// REST de la consola con la credencial que cada empresa configura en ONE.
/// </summary>
public interface IDeviceManagementGateway
{
    /// <summary>
    /// Si el adaptador puede dar las órdenes de jornada (WL_JORNADA y cierre de Shared Device).
    /// Mientras sea false, el control de apps no se activa y la jornada no espera a MobiControl,
    /// aunque la consola responda las lecturas.
    /// </summary>
    bool CanControlWorkday { get; }

    Task<IReadOnlyList<ManagedDevice>> ListDevicesAsync(CancellationToken ct = default);

    Task<IReadOnlyList<VerificationCheck>> VerifyAsync(CancellationToken ct = default);

    Task<IReadOnlyList<string>> ListAppsAsync(CancellationToken ct = default);

    /// <summary>Atributo WL_JORNADA (ABIERTA/CERRADA): MobiControl aplica el perfil que corresponde.</summary>
    Task<GatewayResult> SetWorkdayStateAsync(string deviceId, bool open, CancellationToken ct = default);

    Task<GatewayResult> LogoutSharedAsync(string deviceId, CancellationToken ct = default);
}

/// <summary>Arma un archivo de Excel (.xlsx) con una hoja.</summary>
public interface ISpreadsheetWriter
{
    byte[] Write(string sheetName, IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<object?>> rows);
}

/// <summary>Archivo para descargar.</summary>
public sealed record FileDownload(string FileName, string ContentType, byte[] Content);
