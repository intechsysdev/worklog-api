namespace WorkLog.Application.Abstractions;

/// <summary>Cifra lo que no debe quedar legible en la base, como el secreto de la credencial de ONE.</summary>
public interface ISecretProtector
{
    string Protect(string value);

    /// <summary>Null si ya no se puede descifrar (por ejemplo, si el servidor perdió sus claves).</summary>
    string? Unprotect(string value);
}

/// <summary>Variables de una empresa en ONE, tal como las entrega su API de integración.</summary>
public sealed record OneAppConfiguration(
    Guid TenantId,
    string TenantName,
    IReadOnlyDictionary<string, string?> Settings,
    string ConfigVersion);

/// <summary>La configuración, o por qué no se pudo obtener, en palabras para quien administra.</summary>
public sealed record OneConfigurationResult(OneAppConfiguration? Configuration, string? Problem)
{
    public static OneConfigurationResult Fail(string problem) => new(null, problem);
}

/// <summary>Cliente de la API de integración de ONE (/api/v1/integration/config).</summary>
public interface IOneIntegrationClient
{
    /// <summary>
    /// Lee la configuración con la credencial y comprueba que sea de <paramref name="tenantId"/> y
    /// de WorkLog: una credencial de otra empresa o de otra app se rechaza.
    /// </summary>
    Task<OneConfigurationResult> GetConfigurationAsync(string apiKey, string apiSecret, Guid tenantId, CancellationToken ct = default);
}

/// <summary>Configuración en ONE de la empresa de la petición, en caché unos minutos.</summary>
public interface ITenantConfiguration
{
    Task<OneConfigurationResult> GetAsync(CancellationToken ct = default);

    /// <summary>Descarta lo cacheado de una empresa: se usa al cambiar o quitar su credencial.</summary>
    void Forget(Guid tenantId);
}
