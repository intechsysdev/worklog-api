using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using WorkLog.Application.Abstractions;
using WorkLog.Application.Integrations;
using WorkLog.Application.Organization;
using WorkLog.Domain.Geo;
using WorkLog.Domain.Organization;
using WorkLog.Infrastructure.MobiControl;

namespace WorkLog.UnitTests.Application;

/// <summary>Consola de mentiras que recuerda las geocercas por nombre, como MobiControl.</summary>
internal sealed class FakeGeofences : IGeofenceGateway
{
    public Dictionary<string, IReadOnlyList<GeoPoint>> Console { get; } = [];
    public List<string> Calls { get; } = [];
    public string? Unavailable { get; set; }
    public bool FailCreate { get; set; }

    public Task<string?> UnavailableReasonAsync(CancellationToken ct = default) => Task.FromResult(Unavailable);

    public Task<GeofenceCallResult> CreateAsync(string name, IReadOnlyList<GeoPoint> ring, CancellationToken ct = default)
    {
        Calls.Add($"create {name}");
        if (FailCreate) return Task.FromResult(GeofenceCallResult.Fail("MobiControl rechazó la geocerca (500)."));
        if (Console.ContainsKey(name)) return Task.FromResult(GeofenceCallResult.Fail("Ya existe.", alreadyExists: true));
        Console[name] = ring;
        return Task.FromResult(GeofenceCallResult.Ok($"ref-{Console.Count}"));
    }

    public Task<GeofenceCallResult> RenameAsync(string currentName, string newName, CancellationToken ct = default)
    {
        Calls.Add($"rename {currentName} -> {newName}");
        Console[newName] = Console[currentName];
        Console.Remove(currentName);
        return Task.FromResult(GeofenceCallResult.Ok());
    }

    public Task<GeofenceCallResult> DeleteAsync(string name, CancellationToken ct = default)
    {
        Calls.Add($"delete {name}");
        Console.Remove(name);
        return Task.FromResult(GeofenceCallResult.Ok());
    }
}

public sealed class GeofenceSyncTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeGeofences _console = new();
    private readonly SiteGeofenceSync _sync;

    public GeofenceSyncTests() => _sync = new SiteGeofenceSync(_console, TimeProvider.System);

    private static GeoPoint P(double lat, double lng) => GeoPoint.Create(lat, lng).Value;

    private static WorkSite Site(string name = "Bodega Norte") =>
        WorkSite.Create(Guid.CreateVersion7(), name, null, Geofence.Circle(P(4.7, -74.05), 100).Value).Value;

    [Fact]
    public async Task Un_sitio_nuevo_se_crea_en_MobiControl_con_el_prefijo_de_WorkLog()
    {
        var site = Site();

        await _sync.SyncAsync(site, false, Ct);

        site.MobiControlName.ShouldBe("WorkLog · Bodega Norte");
        site.MobiControlReferenceId.ShouldNotBeNull();
        site.MobiControlError.ShouldBeNull();
        _console.Console["WorkLog · Bodega Norte"].Count.ShouldBe(48);
    }

    [Fact]
    public async Task Cambiar_solo_el_nombre_la_renombra_y_cambiar_la_forma_la_recrea()
    {
        var site = Site();
        await _sync.SyncAsync(site, false, Ct);

        site.Update("Bodega Central", null, site.Geofence);
        await _sync.SyncAsync(site, false, Ct);
        _console.Calls.Last().ShouldBe("rename WorkLog · Bodega Norte -> WorkLog · Bodega Central");
        site.MobiControlName.ShouldBe("WorkLog · Bodega Central");

        site.Update("Bodega Central", null, Geofence.Polygon([P(4.70, -74.05), P(4.71, -74.05), P(4.71, -74.04)]).Value);
        await _sync.SyncAsync(site, true, Ct);
        _console.Calls.TakeLast(2).ShouldBe(["delete WorkLog · Bodega Central", "create WorkLog · Bodega Central"]);
        _console.Console["WorkLog · Bodega Central"].Count.ShouldBe(3);
    }

    [Fact]
    public async Task Desactivar_la_borra_de_la_consola_y_activar_la_vuelve_a_crear()
    {
        var site = Site();
        await _sync.SyncAsync(site, false, Ct);

        site.Deactivate();
        await _sync.SyncAsync(site, false, Ct);
        _console.Console.ShouldBeEmpty();
        site.MobiControlName.ShouldBeNull();

        site.Activate();
        await _sync.SyncAsync(site, false, Ct);
        _console.Console.ShouldContainKey("WorkLog · Bodega Norte");
    }

    [Fact]
    public async Task Si_la_consola_no_esta_disponible_el_sitio_queda_pendiente_con_el_motivo()
    {
        _console.Unavailable = "WorkLog no tiene la credencial de ONE de esta empresa.";
        var site = Site();

        await _sync.SyncAsync(site, false, Ct);

        site.MobiControlName.ShouldBeNull();
        site.MobiControlError.ShouldBe("WorkLog no tiene la credencial de ONE de esta empresa.");
        _console.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Si_ya_existia_una_geocerca_de_WorkLog_con_ese_nombre_la_reemplaza()
    {
        _console.Console["WorkLog · Bodega Norte"] = [P(1, 1), P(1, 2), P(2, 2)];
        var site = Site();

        await _sync.SyncAsync(site, false, Ct);

        _console.Calls.ShouldBe(["create WorkLog · Bodega Norte", "delete WorkLog · Bodega Norte", "create WorkLog · Bodega Norte"]);
        _console.Console["WorkLog · Bodega Norte"].Count.ShouldBe(48);
        site.MobiControlError.ShouldBeNull();
    }

    [Fact]
    public async Task Si_la_consola_rechaza_la_geocerca_el_sitio_igual_existe_y_queda_pendiente()
    {
        _console.FailCreate = true;
        var site = Site();

        await _sync.SyncAsync(site, false, Ct);

        site.MobiControlName.ShouldBeNull();
        site.MobiControlError!.ShouldContain("rechazó");
    }

    [Fact]
    public void El_nombre_en_la_consola_quita_los_caracteres_de_la_ruta_y_no_pasa_de_100()
    {
        WorkSite.MobiControlNameFor("Sede 1/Norte #2 (50%)").ShouldBe("WorkLog · Sede 1 Norte 2 (50 )");
        WorkSite.MobiControlNameFor(new string('x', 150)).Length.ShouldBe(WorkSite.MobiControlNameMaxLength);
    }

    [Fact]
    public void El_circulo_se_envia_como_poligono_de_48_lados_sobre_el_borde()
    {
        var center = P(4.7, -74.05);
        var ring = Geofence.Circle(center, 200).Value.Ring();

        ring.Count.ShouldBe(48);
        ring.ShouldAllBe(p => Math.Abs(Distance(center, p) - 200) < 2);
    }

    private static double Distance(GeoPoint a, GeoPoint b)
    {
        const double r = 6_371_000;
        var dLat = (b.Latitude - a.Latitude) * Math.PI / 180;
        var dLng = (b.Longitude - a.Longitude) * Math.PI / 180;
        var h = Math.Pow(Math.Sin(dLat / 2), 2) +
                Math.Cos(a.Latitude * Math.PI / 180) * Math.Cos(b.Latitude * Math.PI / 180) * Math.Pow(Math.Sin(dLng / 2), 2);
        return 2 * r * Math.Asin(Math.Sqrt(h));
    }

    // ── Adaptador REST contra una consola de mentiras ─────────────────────

    private static (MobiControlRestGateway Gateway, StubHttp Http) Rest(Func<HttpRequestMessage, HttpResponseMessage> console)
    {
        var tenant = Substitute.For<ITenantContext>();
        tenant.TenantId.Returns(Guid.CreateVersion7());
        var configuration = Substitute.For<ITenantConfiguration>();
        configuration.GetAsync(Arg.Any<CancellationToken>()).Returns(new OneConfigurationResult(new OneAppConfiguration(Guid.CreateVersion7(), "INTECHSYS",
            new Dictionary<string, string?>
            {
                [MobiControlSettings.BaseUrl] = "https://s1.mobicontrolcloud.com/mobicontrol",
                [MobiControlSettings.ClientId] = "c",
                [MobiControlSettings.ClientSecret] = "s",
                [MobiControlSettings.User] = "u",
                [MobiControlSettings.Password] = "p"
            }, "v1"), null));
        var stub = new StubHttp(request => request.RequestUri!.AbsolutePath.EndsWith("/api/token", StringComparison.Ordinal)
            ? StubHttp.Json("""{"access_token":"tok","expires_in":3600}""")
            : console(request));
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(MobiControlRestGateway.HttpClientName).Returns(_ => new HttpClient(stub, disposeHandler: false));
        return (new MobiControlRestGateway(factory, tenant, configuration, new MemoryCache(new MemoryCacheOptions()),
            NullLogger<MobiControlRestGateway>.Instance), stub);
    }

    [Fact]
    public async Task Crear_manda_el_poligono_cerrado_con_los_nombres_de_MobiControl()
    {
        var (gateway, http) = Rest(request => request.Method == HttpMethod.Get
            ? StubHttp.Json("""{"Name":"WorkLog · Bodega","ReferenceId":"abc-123","Vertices":[]}""")
            : new HttpResponseMessage(HttpStatusCode.OK));

        var result = await gateway.CreateAsync("WorkLog · Bodega", [P(4.70, -74.05), P(4.71, -74.05), P(4.71, -74.04)], Ct);

        result.Succeeded.ShouldBeTrue();
        result.ReferenceId.ShouldBe("abc-123");
        var post = http.Calls.Single(c => c.Request.Method == HttpMethod.Post && c.Request.RequestUri!.AbsolutePath.EndsWith("/geofences", StringComparison.Ordinal));
        using var body = JsonDocument.Parse(post.Body!);
        body.RootElement.GetProperty("Name").GetString().ShouldBe("WorkLog · Bodega");
        var vertices = body.RootElement.GetProperty("Vertices");
        vertices.GetArrayLength().ShouldBe(4);
        vertices[3].GetProperty("Latitude").GetDouble().ShouldBe(vertices[0].GetProperty("Latitude").GetDouble());
    }

    [Fact]
    public async Task Los_errores_de_MobiControl_se_traducen_y_borrar_una_que_no_existe_es_exito()
    {
        var (gateway, _) = Rest(request => request.Method == HttpMethod.Post
            ? StubHttp.Json("""{"ErrorCode":3200,"Message":"Exists"}""", HttpStatusCode.BadRequest)
            : new HttpResponseMessage(HttpStatusCode.Forbidden));

        var created = await gateway.CreateAsync("WorkLog · Bodega", [P(4.70, -74.05), P(4.71, -74.05), P(4.71, -74.04)], Ct);
        created.AlreadyExists.ShouldBeTrue();

        (await gateway.DeleteAsync("WorkLog · Bodega", Ct)).Succeeded.ShouldBeTrue();
        (await gateway.UnavailableReasonAsync(Ct)).ShouldBeNull();
    }
}
