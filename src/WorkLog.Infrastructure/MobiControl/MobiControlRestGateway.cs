using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using WorkLog.Application.Abstractions;
using WorkLog.Application.Integrations;
using WorkLog.Domain.Devices;

namespace WorkLog.Infrastructure.MobiControl;

/// <summary>
/// Consola de MobiControl de cada empresa por su API REST, con las variables que la empresa tiene
/// en ONE. Por ahora solo lee (equipos y verificación de la conexión): no da órdenes de jornada,
/// así que el control de apps no se puede activar y la jornada nunca espera a la consola.
/// El token y la búsqueda de equipos siguen al cliente de geomonitoreo-api (ClienteMobiControl).
/// </summary>
internal sealed class MobiControlRestGateway(
    IHttpClientFactory httpFactory,
    ITenantContext tenant,
    ITenantConfiguration configuration,
    IMemoryCache cache,
    ILogger<MobiControlRestGateway> logger) : IDeviceManagementGateway
{
    public const string HttpClientName = "mobicontrol";

    private const int PageSize = 500;

    /// <summary>Tope de equipos que se leen por empresa, para que una consola enorme no tumbe Equipo hoy.</summary>
    private const int MaxDevices = 5000;

    /// <summary>Equipo hoy y el panel piden la lista seguido; un minuto basta para estado y batería.</summary>
    private static readonly TimeSpan DevicesFresh = TimeSpan.FromMinutes(1);

    private const string NoCommands =
        "WorkLog todavía solo lee la consola: aún no envía el atributo WL_JORNADA ni cierra Shared Device. " +
        "El control de apps por jornada se habilita cuando esté esa parte.";

    // El token dura cerca de una hora y es de la consola de cada empresa: se reutiliza por empresa
    // y se descarta si cambian las variables (otra consola, otro usuario u otro client ID).
    private static readonly ConcurrentDictionary<Guid, CachedToken> Tokens = new();
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> TokenLocks = new();

    private sealed record CachedToken(string Fingerprint, string Token, DateTimeOffset ExpiresAt);

    public bool CanControlWorkday => false;

    public async Task<IReadOnlyList<ManagedDevice>> ListDevicesAsync(CancellationToken ct = default)
    {
        if (tenant.TenantId is not { } tenantId) return [];

        var key = $"mc:devices:{tenantId:N}";
        if (cache.TryGetValue<IReadOnlyList<ManagedDevice>>(key, out var cached) && cached is not null)
            return cached;

        var session = await OpenAsync(tenantId, ct);
        if (session.Token is null)
        {
            logger.LogInformation("Sin equipos de MobiControl para {Tenant}: {Problem}", tenantId, session.Problem);
            return [];
        }

        try
        {
            var devices = await FetchDevicesAsync(session, ct);
            cache.Set(key, devices, DevicesFresh);
            return devices;
        }
        catch (ConsoleException ex)
        {
            // Una consola caída no puede tumbar Equipo hoy: se muestra sin estado de equipos.
            logger.LogWarning("No se pudieron leer los equipos de MobiControl de {Tenant}: {Problem}", tenantId, ex.Message);
            return [];
        }
    }

    public async Task<IReadOnlyList<VerificationCheck>> VerifyAsync(CancellationToken ct = default)
    {
        var commands = new VerificationCheck("workday_commands", "Órdenes de jornada (WL_JORNADA y Shared Device)", false, NoCommands);
        if (tenant.TenantId is not { } tenantId)
            return [new("one_link", "Vínculo con INTECHSYS ONE", false, "No hay empresa en la petición."), commands];

        var config = await configuration.GetAsync(ct);
        if (config.Configuration is not { } settings)
            return
            [
                new("one_link", "Vínculo con INTECHSYS ONE", false, config.Problem ?? "ONE no respondió."),
                new("connection", "Conexión con MobiControl", false, "Primero hay que vincular WorkLog con ONE."),
                commands
            ];

        var link = new VerificationCheck("one_link", "Vínculo con INTECHSYS ONE", true, $"Credencial de {settings.TenantName}.");
        var connection = MobiControlSettings.Read(settings.Settings);
        if (connection is null)
        {
            var missing = MobiControlSettings.Missing(settings.Settings);
            var detail = missing.Count > 0
                ? $"En ONE faltan las variables de WorkLog: {string.Join(", ", missing)}."
                : $"{MobiControlSettings.BaseUrl} debe ser una dirección https completa.";
            return [link, new("connection", "Conexión con MobiControl", false, detail), commands];
        }

        var (token, problem) = await TokenAsync(tenantId, connection, ct);
        if (token is null)
            return [link, new("connection", "Conexión con MobiControl", false, problem!), commands];

        try
        {
            var total = await CountDevicesAsync(new Session(tenantId, connection, token, null), ct);
            cache.Remove($"mc:devices:{tenantId:N}");
            return [link, new("connection", "Conexión con MobiControl", true, $"Consola {connection.BaseUrl.Host} · {total} equipo(s) inscrito(s)."), commands];
        }
        catch (ConsoleException ex)
        {
            return [link, new("connection", "Conexión con MobiControl", false, ex.Message), commands];
        }
    }

    public Task<IReadOnlyList<string>> ListAppsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>([]);

    public Task<GatewayResult> SetWorkdayStateAsync(string deviceId, bool open, CancellationToken ct = default) =>
        Task.FromResult(new GatewayResult(DeviceCommandStatus.Failed, NoCommands));

    public Task<GatewayResult> LogoutSharedAsync(string deviceId, CancellationToken ct = default) =>
        Task.FromResult(new GatewayResult(DeviceCommandStatus.Failed, NoCommands));

    // ── Sesión y token ──────────────────────────────────────────────────────

    private sealed record Session(Guid TenantId, MobiControlConnection? Connection, string? Token, string? Problem);

    private async Task<Session> OpenAsync(Guid tenantId, CancellationToken ct)
    {
        var config = await configuration.GetAsync(ct);
        if (config.Configuration is not { } settings) return new Session(tenantId, null, null, config.Problem);

        var connection = MobiControlSettings.Read(settings.Settings);
        if (connection is null) return new Session(tenantId, null, null, "Faltan variables de MobiControl en ONE.");

        var (token, problem) = await TokenAsync(tenantId, connection, ct);
        return new Session(tenantId, connection, token, problem);
    }

    private static string Fingerprint(MobiControlConnection c) => $"{c.BaseUrl}|{c.ClientId}|{c.User}";

    private async Task<(string? Token, string? Problem)> TokenAsync(Guid tenantId, MobiControlConnection connection, CancellationToken ct)
    {
        var fingerprint = Fingerprint(connection);
        if (Tokens.TryGetValue(tenantId, out var cached) && cached.Fingerprint == fingerprint && DateTimeOffset.UtcNow < cached.ExpiresAt)
            return (cached.Token, null);

        var gate = TokenLocks.GetOrAdd(tenantId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (Tokens.TryGetValue(tenantId, out cached) && cached.Fingerprint == fingerprint && DateTimeOffset.UtcNow < cached.ExpiresAt)
                return (cached.Token, null);

            using var limit = Limit(connection, ct);
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(connection.BaseUrl, "api/token"))
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "password",
                    ["username"] = connection.User,
                    ["password"] = connection.Password
                })
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{connection.ClientId}:{connection.ClientSecret}")));

            using var response = await httpFactory.CreateClient(HttpClientName).SendAsync(request, limit.Token);
            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                logger.LogWarning("MobiControl rechazó el token de {Tenant} ({Status}).", tenantId, status);
                return (null, status is 400 or 401 or 403
                    ? $"La consola {connection.BaseUrl.Host} rechazó el ingreso ({status}). Revisa en ONE el usuario, la contraseña, el client ID y el client secret."
                    : $"La consola {connection.BaseUrl.Host} respondió {status} al pedir el token.");
            }

            var body = await response.Content.ReadFromJsonAsync<TokenResponse>(limit.Token);
            if (string.IsNullOrWhiteSpace(body?.AccessToken))
                return (null, $"La consola {connection.BaseUrl.Host} no entregó un token.");

            // Un minuto de colchón para no usar un token que vence en pleno viaje.
            Tokens[tenantId] = new CachedToken(fingerprint, body.AccessToken,
                DateTimeOffset.UtcNow.AddSeconds(Math.Max(120, body.ExpiresIn) - 60));
            return (body.AccessToken, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (null, $"La consola {connection.BaseUrl.Host} no respondió a tiempo.");
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Sin conexión con MobiControl de {Tenant}.", tenantId);
            return (null, $"No se pudo conectar con la consola {connection.BaseUrl.Host}.");
        }
        finally
        {
            gate.Release();
        }
    }

    // ── Equipos ─────────────────────────────────────────────────────────────

    private async Task<IReadOnlyList<ManagedDevice>> FetchDevicesAsync(Session session, CancellationToken ct)
    {
        var devices = new List<ManagedDevice>();
        while (devices.Count < MaxDevices)
        {
            var (page, total) = await SearchAsync(session, devices.Count, PageSize, ct);
            devices.AddRange(page);
            if (page.Count < PageSize || (total is { } t && devices.Count >= t)) break;
        }

        return devices;
    }

    /// <summary>take=0: la consola responde solo el total, en la cabecera x-total-count.</summary>
    private async Task<int> CountDevicesAsync(Session session, CancellationToken ct)
    {
        var (page, total) = await SearchAsync(session, 0, 0, ct);
        return total ?? page.Count;
    }

    private async Task<(IReadOnlyList<ManagedDevice> Page, int? Total)> SearchAsync(Session session, int skip, int take, CancellationToken ct)
    {
        var connection = session.Connection!;
        var path = $"api/devices/search?skip={skip}&take={take}&order={Uri.EscapeDataString("+DeviceName")}";

        HttpResponseMessage response;
        using var limit = Limit(connection, ct);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(connection.BaseUrl, path));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            response = await httpFactory.CreateClient(HttpClientName).SendAsync(request, limit.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ConsoleException($"La consola {connection.BaseUrl.Host} no respondió a tiempo al listar los equipos.");
        }
        catch (HttpRequestException)
        {
            throw new ConsoleException($"No se pudo conectar con la consola {connection.BaseUrl.Host}.");
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                // Un token revocado antes de tiempo: el siguiente intento pide uno nuevo.
                Tokens.TryRemove(session.TenantId, out _);
                throw new ConsoleException($"La consola {connection.BaseUrl.Host} dejó de aceptar el token. Intenta de nuevo.");
            }

            if (!response.IsSuccessStatusCode)
                throw new ConsoleException($"La consola {connection.BaseUrl.Host} respondió {(int)response.StatusCode} al listar los equipos.");

            int? total = response.Headers.TryGetValues("x-total-count", out var values)
                         && int.TryParse(values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count)
                ? count
                : null;

            var text = await response.Content.ReadAsStringAsync(limit.Token);
            if (string.IsNullOrWhiteSpace(text)) return ([], total);

            using var json = JsonDocument.Parse(text);
            if (json.RootElement.ValueKind != JsonValueKind.Array) return ([], total);

            var page = new List<ManagedDevice>(json.RootElement.GetArrayLength());
            foreach (var item in json.RootElement.EnumerateArray())
            {
                if (ToDevice(item) is { } device) page.Add(device);
            }

            return (page, total);
        }
    }

    /// <summary>
    /// Campos comunes a todas las plataformas (MobiControl devuelve un tipo por plataforma). Si el
    /// equipo es Shared Device se sabrá cuando WorkLog lea los perfiles: por ahora se toma como asignado.
    /// </summary>
    internal static ManagedDevice? ToDevice(JsonElement item)
    {
        if (Text(item, "DeviceId") is not { } id) return null;

        var model = string.Join(' ', new[] { Text(item, "Manufacturer"), Text(item, "Model") }.Where(part => part is not null));
        return new ManagedDevice(
            id,
            model.Length == 0 ? "Equipo" : model,
            Text(item, "HardwareSerialNumber") ?? Text(item, "DeviceName") ?? id,
            Text(item, "Path") ?? string.Empty,
            false,
            Bool(item, "IsAgentOnline") ?? false,
            Int(item, "BatteryStatus") is { } battery and >= 0 and <= 100 ? battery : null,
            Date(item, "LastCheckInTime") ?? Date(item, "LastAgentConnectTime"),
            Date(item, "EnrollmentTime"));
    }

    private static CancellationTokenSource Limit(MobiControlConnection connection, CancellationToken ct)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(ct);
        source.CancelAfter(TimeSpan.FromSeconds(connection.TimeoutSeconds));
        return source;
    }

    private static string? Text(JsonElement item, string field) =>
        item.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;

    private static bool? Bool(JsonElement item, string field) =>
        item.TryGetProperty(field, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

    private static int? Int(JsonElement item, string field) =>
        item.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;

    /// <summary>ISO 8601; sin zona se toma como UTC, que es como las guarda la consola.</summary>
    private static DateTimeOffset? Date(JsonElement item, string field) =>
        Text(item, field) is { } text
        && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date)
        && date.Year > 2000
            ? date
            : null;

    private sealed class ConsoleException(string message) : Exception(message);

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);
}
