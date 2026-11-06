using System.ComponentModel.DataAnnotations;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WorkLog.Application.Abstractions;

namespace WorkLog.Infrastructure.One;

/// <summary>Conexión con INTECHSYS ONE, que es quien emite y valida las sesiones.</summary>
public sealed class OneOptions
{
    public const string SectionName = "One";

    /// <summary>Raíz del API de ONE (p. ej. http://localhost:5180/).</summary>
    [Required, Url]
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Slug de WorkLog en el catálogo de apps de ONE.</summary>
    [Required]
    public string AppSlug { get; set; } = "worklog";

    [Range(2, 60)]
    public int TimeoutSeconds { get; set; } = 10;
}

/// <summary>
/// Adaptador del puerto de identidad: pregunta a ONE con el mismo token del usuario. No verifica
/// firmas aquí a propósito: eso exigiría tener la llave con que ONE firma los tokens de toda la
/// plataforma, y quien la leyera podría fabricar sesiones de cualquiera.
/// </summary>
internal sealed class OneIdentityProvider(HttpClient http, IOptions<OneOptions> options, ILogger<OneIdentityProvider> logger) : IIdentityProvider
{
    public async Task<OneIdentity?> GetIdentityAsync(string accessToken, CancellationToken ct = default)
    {
        var profile = await GetAsync<ProfileResponse>("api/v1/auth/me", accessToken, ct);
        return profile is null
            ? null
            : new OneIdentity(profile.Id, profile.Email, profile.FullName ?? profile.Email, profile.IsPlatformAdmin);
    }

    public async Task<IReadOnlyList<OneTenantAccess>?> GetTenantsAsync(string accessToken, CancellationToken ct = default)
    {
        var slug = Uri.EscapeDataString(options.Value.AppSlug);
        var tenants = await GetAsync<List<TenantResponse>>($"api/v1/me/apps/{slug}/tenants", accessToken, ct);
        return tenants?.Select(t => new OneTenantAccess(t.TenantId, t.Name, t.Slug, t.Role)).ToList();
    }

    public async Task<OneProvisionResult> ProvisionMemberAsync(string accessToken, Guid tenantId, string email, string firstName,
        string lastName, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"api/v1/tenants/{tenantId}/members/provision")
            {
                Content = JsonContent.Create(new { email, firstName, lastName, role = "Member" })
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var response = await http.SendAsync(request, ct);
            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                return OneProvisionResult.Fail("Sin permiso para dar de alta personas en la empresa.", forbidden: true);

            if (!response.IsSuccessStatusCode)
            {
                var problem = await response.Content.ReadFromJsonAsync<ProblemResponse>(ct);
                return OneProvisionResult.Fail(problem?.Detail ?? problem?.Title ?? $"ONE respondió {(int)response.StatusCode}.");
            }

            var body = await response.Content.ReadFromJsonAsync<ProvisionResponse>(ct);
            return body is null
                ? OneProvisionResult.Fail("ONE no devolvió el resultado del alta.")
                : OneProvisionResult.Ok(body.UserId, body.UserCreated, body.TemporaryPassword);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogError(ex, "No se pudo dar de alta a {Email} en ONE.", email);
            return OneProvisionResult.Fail("ONE no respondió. Intenta de nuevo en un momento.");
        }
    }

    private sealed record ProvisionResponse(
        [property: JsonPropertyName("userId")] Guid UserId,
        [property: JsonPropertyName("userCreated")] bool UserCreated,
        [property: JsonPropertyName("temporaryPassword")] string? TemporaryPassword);

    private sealed record ProblemResponse(
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("detail")] string? Detail);

    private async Task<T?> GetAsync<T>(string path, string accessToken, CancellationToken ct) where T : class
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogDebug("ONE respondió {Status} a {Path}.", (int)response.StatusCode, path);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<T>(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Si ONE no responde nadie puede entrar: se registra como error de dependencia.
            logger.LogError(ex, "No se pudo consultar {Path} en ONE.", path);
            return null;
        }
    }

    private sealed record ProfileResponse(
        [property: JsonPropertyName("id")] Guid Id,
        [property: JsonPropertyName("email")] string Email,
        [property: JsonPropertyName("fullName")] string? FullName,
        [property: JsonPropertyName("isPlatformAdmin")] bool IsPlatformAdmin);

    private sealed record TenantResponse(
        [property: JsonPropertyName("tenantId")] Guid TenantId,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("slug")] string? Slug,
        [property: JsonPropertyName("role")] string? Role);
}
