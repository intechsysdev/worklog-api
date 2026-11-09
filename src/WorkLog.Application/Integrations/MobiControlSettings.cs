namespace WorkLog.Application.Integrations;

/// <summary>Conexión a la consola de MobiControl de una empresa, armada con sus variables de ONE.</summary>
public sealed record MobiControlConnection(
    Uri BaseUrl,
    string ClientId,
    string ClientSecret,
    string User,
    string Password,
    int TimeoutSeconds);

/// <summary>
/// Las variables de MobiControl en ONE. Las claves son el contrato con el esquema de la app
/// "worklog" en ONE (WorkLogContrato) y son las mismas de Geomonitoreo.
/// </summary>
public static class MobiControlSettings
{
    public const string BaseUrl = "MOBICONTROL_BASE_URL";
    public const string ClientId = "MOBICONTROL_CLIENT_ID";
    public const string ClientSecret = "MOBICONTROL_CLIENT_SECRET";
    public const string User = "MOBICONTROL_USUARIO";
    public const string Password = "MOBICONTROL_PASSWORD";
    public const string TimeoutSeconds = "MOBICONTROL_TIMEOUT_SEGUNDOS";

    private static readonly string[] Required = [BaseUrl, ClientId, ClientSecret, User, Password];

    /// <summary>Las variables obligatorias que faltan o están vacías.</summary>
    public static IReadOnlyList<string> Missing(IReadOnlyDictionary<string, string?> settings) =>
        Required.Where(key => Value(settings, key) is null).ToList();

    /// <summary>Null si falta alguna variable o la URL no es una dirección https (o http en localhost).</summary>
    public static MobiControlConnection? Read(IReadOnlyDictionary<string, string?> settings)
    {
        if (Missing(settings).Count > 0) return null;
        if (!Uri.TryCreate(Value(settings, BaseUrl)!.TrimEnd('/') + "/", UriKind.Absolute, out var url)) return null;
        if (url.Scheme != Uri.UriSchemeHttps && !(url.Scheme == Uri.UriSchemeHttp && url.IsLoopback)) return null;

        var timeout = int.TryParse(Value(settings, TimeoutSeconds), out var seconds) ? Math.Clamp(seconds, 5, 60) : 25;
        return new MobiControlConnection(url, Value(settings, ClientId)!, Value(settings, ClientSecret)!,
            Value(settings, User)!, Value(settings, Password)!, timeout);
    }

    private static string? Value(IReadOnlyDictionary<string, string?> settings, string key) =>
        settings.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
}
