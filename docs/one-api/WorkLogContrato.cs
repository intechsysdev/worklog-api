// PROPUESTA para one-api (no está aplicada). Se copia a
// F:\Intechsys_Proj\one\one-api\src\One.Infrastructure\Persistence\WorkLogContrato.cs y se llama desde
// DbSeeder.RunAsync con un EnsureWorkLogAppAsync igual a EnsureGeomonitoreoAppAsync.
// Además: agregar el origen del panel de WorkLog a Cors:AllowedOrigins de one-api (el navegador llama
// directo a /api/v1/sso/token y /api/v1/auth/refresh) y asignar la app a cada empresa en el portal.

using One.Domain.Entities;
using One.Domain.Enums;

namespace One.Infrastructure.Persistence;

/// <summary>
/// Lo que One necesita saber de WorkLog: su alta en el catálogo y las variables que lee de
/// /integration/config. Las claves son el contrato con work_log_api.
/// </summary>
internal static class WorkLogContrato
{
    public const string Slug = "worklog";

    // TODO: reemplazar por las URL reales cuando existan los App Service / Static Web App de WorkLog.
    public const string ApiBaseUrl = "https://<api-worklog>.azurewebsites.net";
    private const string Panel = "https://<panel-worklog>.azurestaticapps.net";
    public const string LaunchUrl = Panel + "/sso";

    // Una por línea: producción, panel en desarrollo y la app móvil (esquema propio para el SSO desde Flutter).
    public const string RedirectUris =
        Panel + "/sso/callback\n" +
        "http://localhost:5174/sso/callback\n" +
        "com.intechsys.worklog://sso/callback";

    public static AppDefinition App(Guid creadoPor) => new()
    {
        Name = "WorkLog",
        Slug = Slug,
        Description = "Bitácora de actividades: jornada, actividades de campo, horas y aprobaciones.",
        Category = "Operaciones",
        Color = "#AF1839",
        Version = "1.0.0",
        IsActive = true,
        IsPublic = true,
        AvailableScopes = "config.read",
        CreatedBy = creadoPor,
    };

    /// <summary>MobiControl con las mismas claves que Geomonitoreo: una empresa las configura una vez por app.</summary>
    public static AppSettingDefinition[] Variables(Guid appId) =>
    [
        new()
        {
            AppId = appId, Key = "MOBICONTROL_BASE_URL", Label = "URL de la consola de MobiControl",
            Description = "Raíz de la API de MobiControl. Vacía: WorkLog no controla los dispositivos.",
            Placeholder = "https://sXXXXXX.mobicontrolcloud.com/mobicontrol",
            DataType = SettingDataType.Url, Group = "MobiControl", DisplayOrder = 1
        },
        new()
        {
            AppId = appId, Key = "MOBICONTROL_CLIENT_ID", Label = "Client ID",
            DataType = SettingDataType.Secret, IsSecret = true, Group = "MobiControl", DisplayOrder = 2
        },
        new()
        {
            AppId = appId, Key = "MOBICONTROL_CLIENT_SECRET", Label = "Client secret",
            DataType = SettingDataType.Secret, IsSecret = true, Group = "MobiControl", DisplayOrder = 3
        },
        new()
        {
            AppId = appId, Key = "MOBICONTROL_USUARIO", Label = "Usuario de la API",
            Description = "Usuario con permiso para aplicar perfiles y atributos personalizados (WL_JORNADA).",
            DataType = SettingDataType.String, Group = "MobiControl", DisplayOrder = 4
        },
        new()
        {
            AppId = appId, Key = "MOBICONTROL_PASSWORD", Label = "Contraseña de la API",
            DataType = SettingDataType.Secret, IsSecret = true, Group = "MobiControl", DisplayOrder = 5
        },
        new()
        {
            AppId = appId, Key = "STORAGE_CONTAINER", Label = "Contenedor de evidencias",
            Description = "Contenedor de Azure Blob Storage donde se guardan fotos, firmas y soportes de la empresa.",
            DataType = SettingDataType.String, DefaultValue = "worklog", Group = "Almacenamiento", DisplayOrder = 6
        }
    ];
}
