using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WorkLog.Application.Abstractions;

namespace WorkLog.Infrastructure.One;

/// <summary>
/// API de integración de ONE: con la credencial de una empresa devuelve sus variables. Comprueba
/// que la credencial sea de esa empresa y de WorkLog, porque ONE la acepta igual si es de otra.
/// </summary>
internal sealed class OneIntegrationClient(HttpClient http, IOptions<OneOptions> options, ILogger<OneIntegrationClient> logger)
    : IOneIntegrationClient
{
    public async Task<OneConfigurationResult> GetConfigurationAsync(string apiKey, string apiSecret, Guid tenantId, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "api/v1/integration/config");
            request.Headers.Add("X-Api-Key", apiKey);
            request.Headers.Add("X-Api-Secret", apiSecret);

            using var response = await http.SendAsync(request, ct);
            if (response.StatusCode is HttpStatusCode.Unauthorized)
                return OneConfigurationResult.Fail("ONE no reconoce la credencial: revisa la API key y el secreto, o si fue rotada o revocada.");
            if (response.StatusCode is HttpStatusCode.Forbidden)
                return OneConfigurationResult.Fail("ONE rechazó la credencial: puede estar revocada, o WorkLog estar desactivada o vencida en la empresa.");
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("ONE respondió {Status} a /integration/config.", (int)response.StatusCode);
                return OneConfigurationResult.Fail($"ONE respondió {(int)response.StatusCode}. Intenta de nuevo en un momento.");
            }

            var body = await response.Content.ReadFromJsonAsync<ConfigResponse>(ct);
            if (body?.Tenant is null || body.App is null)
                return OneConfigurationResult.Fail("ONE respondió algo que WorkLog no entiende.");

            if (!string.Equals(body.App.Slug, options.Value.AppSlug, StringComparison.OrdinalIgnoreCase))
                return OneConfigurationResult.Fail($"La credencial es de la app «{body.App.Name}», no de WorkLog. Crea una en ONE para WorkLog.");
            if (body.Tenant.Id != tenantId)
                return OneConfigurationResult.Fail($"La credencial es de la empresa «{body.Tenant.Name}», no de esta.");

            return new OneConfigurationResult(
                new OneAppConfiguration(body.Tenant.Id, body.Tenant.Name, body.Settings ?? [], body.ConfigVersion ?? string.Empty), null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException
                                   && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "No se pudo consultar /integration/config de ONE.");
            return OneConfigurationResult.Fail("No se pudo consultar ONE. Intenta de nuevo en un momento.");
        }
    }

    private sealed record ConfigResponse(
        [property: JsonPropertyName("tenant")] TenantPart? Tenant,
        [property: JsonPropertyName("app")] AppPart? App,
        [property: JsonPropertyName("settings")] Dictionary<string, string?>? Settings,
        [property: JsonPropertyName("configVersion")] string? ConfigVersion);

    private sealed record TenantPart(
        [property: JsonPropertyName("id")] Guid Id,
        [property: JsonPropertyName("name")] string Name);

    private sealed record AppPart(
        [property: JsonPropertyName("slug")] string Slug,
        [property: JsonPropertyName("name")] string Name);
}
