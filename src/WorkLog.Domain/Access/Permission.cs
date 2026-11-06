namespace WorkLog.Domain.Access;

/// <summary>Permisos de WorkLog. Se guardan como banderas en el rol.</summary>
[Flags]
public enum Permission
{
    None = 0,

    /// <summary>Ver su propia jornada, semana y solicitudes.</summary>
    ViewOwn = 1 << 0,

    /// <summary>Registrar jornada y actividades en la app.</summary>
    RecordWork = 1 << 1,

    /// <summary>Ver Equipo hoy y las horas del equipo.</summary>
    ViewTeam = 1 << 2,

    /// <summary>Ver los puntos de ubicación del equipo. Cada consulta queda auditada.</summary>
    ViewLocations = 1 << 3,

    /// <summary>Aprobar ajustes, horas extra y cambios de turno.</summary>
    ApproveRequests = 1 << 4,

    /// <summary>Registrar y aprobar novedades.</summary>
    ManageAbsences = 1 << 5,

    /// <summary>Cerrar el periodo.</summary>
    ClosePeriods = 1 << 6,

    /// <summary>Exportar para nómina.</summary>
    ExportPayroll = 1 << 7,

    /// <summary>Plantillas de horario y asignaciones.</summary>
    ManageSchedules = 1 << 8,

    /// <summary>Parámetros, actividades, sitios y órdenes de trabajo.</summary>
    ManageSettings = 1 << 9,

    /// <summary>Invitar usuarios y cambiar roles.</summary>
    ManageUsers = 1 << 10,

    /// <summary>Consultar la auditoría.</summary>
    ViewAudit = 1 << 11,

    All = (1 << 12) - 1
}

/// <summary>Metadatos de cada permiso para la matriz del panel.</summary>
public sealed record PermissionInfo(Permission Permission, string Key, string Group, string Name, string Description)
{
    public static readonly IReadOnlyList<PermissionInfo> Catalog =
    [
        new(Permission.ViewOwn, "view_own", "Mi información", "Ver lo propio", "Su jornada, su semana y sus solicitudes."),
        new(Permission.RecordWork, "record_work", "Mi información", "Registrar en la app", "Iniciar jornada y registrar actividades."),
        new(Permission.ViewTeam, "view_team", "Equipo", "Ver el equipo", "Equipo hoy y horas del equipo."),
        new(Permission.ViewLocations, "view_locations", "Equipo", "Ver ubicaciones", "Puntos de ubicación del equipo. Queda en auditoría."),
        new(Permission.ApproveRequests, "approve_requests", "Equipo", "Aprobar solicitudes", "Ajustes, horas extra y cambios de turno."),
        new(Permission.ManageAbsences, "manage_absences", "Periodo", "Novedades", "Registrar y aprobar vacaciones, incapacidades y permisos."),
        new(Permission.ClosePeriods, "close_periods", "Periodo", "Cerrar periodo", "Congelar las horas de la quincena."),
        new(Permission.ExportPayroll, "export_payroll", "Periodo", "Exportar para nómina", "Descargar el periodo en Excel o CSV."),
        new(Permission.ManageSchedules, "manage_schedules", "Configuración", "Horarios", "Plantillas y asignaciones."),
        new(Permission.ManageSettings, "manage_settings", "Configuración", "Parámetros y catálogos", "Parámetros, actividades, sitios y órdenes de trabajo."),
        new(Permission.ManageUsers, "manage_users", "Configuración", "Usuarios", "Invitar personas y cambiar roles."),
        new(Permission.ViewAudit, "view_audit", "Configuración", "Auditoría", "Consultar el registro de auditoría.")
    ];

    public static PermissionInfo? FromKey(string key) =>
        Catalog.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));
}
