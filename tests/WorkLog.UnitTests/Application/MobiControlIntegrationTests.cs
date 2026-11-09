using System.Net;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using WorkLog.Application.Abstractions;
using WorkLog.Application.Integrations;
using WorkLog.Infrastructure.MobiControl;
using WorkLog.Infrastructure.One;

namespace WorkLog.UnitTests.Application;

/// <summary>Responde como lo harían ONE o la consola de MobiControl, y guarda lo que se le pidió.</summary>
internal sealed class StubHttp(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<(HttpRequestMessage Request, string? Body)> Calls { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls.Add((request, request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
        return respond(request);
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

public sealed class MobiControlIntegrationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Dictionary<string, string?> Variables() => new()
    {
        [MobiControlSettings.BaseUrl] = "https://s123.mobicontrolcloud.com/mobicontrol",
        [MobiControlSettings.ClientId] = "cliente",
        [MobiControlSettings.ClientSecret] = "secreto",
        [MobiControlSettings.User] = "api.worklog",
        [MobiControlSettings.Password] = "clave"
    };

    [Fact]
    public void Las_variables_de_MobiControl_exigen_las_cinco_y_una_direccion_https()
    {
        var all = Variables();
        MobiControlSettings.Read(all).ShouldNotBeNull().BaseUrl.ToString().ShouldBe("https://s123.mobicontrolcloud.com/mobicontrol/");

        MobiControlSettings.Missing(new Dictionary<string, string?>(all) { [MobiControlSettings.Password] = " " })
            .ShouldBe([MobiControlSettings.Password]);
        MobiControlSettings.Read(new Dictionary<string, string?>(all) { [MobiControlSettings.BaseUrl] = "http://consola.empresa.com" })
            .ShouldBeNull();
    }

    private static (MobiControlRestGateway Gateway, StubHttp Console) Gateway(OneConfigurationResult config, Func<HttpRequestMessage, HttpResponseMessage> console)
    {
        var tenant = Substitute.For<ITenantContext>();
        tenant.TenantId.Returns(Guid.CreateVersion7()); // Empresa nueva por prueba: el token se cachea por empresa.
        var configuration = Substitute.For<ITenantConfiguration>();
        configuration.GetAsync(Arg.Any<CancellationToken>()).Returns(config);

        var stub = new StubHttp(console);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(MobiControlRestGateway.HttpClientName).Returns(_ => new HttpClient(stub, disposeHandler: false));

        return (new MobiControlRestGateway(factory, tenant, configuration, new MemoryCache(new MemoryCacheOptions()),
            NullLogger<MobiControlRestGateway>.Instance), stub);
    }

    private static OneConfigurationResult Linked() =>
        new(new OneAppConfiguration(Guid.CreateVersion7(), "INTECHSYS", Variables(), "v1"), null);

    private static HttpResponseMessage Console(HttpRequestMessage request)
    {
        if (request.RequestUri!.AbsolutePath.EndsWith("/api/token", StringComparison.Ordinal))
            return StubHttp.Json("""{"access_token":"tok-1","expires_in":3600}""");

        var response = StubHttp.Json("""
            [{"$type":"DeviceAndroidForWork","DeviceId":"dev-1","DeviceName":"Tecnico 01","Manufacturer":"Zebra","Model":"TC26",
              "HardwareSerialNumber":"R58N","Path":"\\\\Empresa\\Campo","IsAgentOnline":true,"BatteryStatus":81,
              "LastCheckInTime":"2026-11-09T14:00:00","EnrollmentTime":"2026-10-01T12:00:00Z"},
             {"DeviceId":"dev-2","IsAgentOnline":false,"BatteryStatus":-1}]
            """);
        response.Headers.Add("x-total-count", "2");
        return response;
    }

    [Fact]
    public async Task Lee_los_equipos_de_la_consola_con_el_token_de_la_empresa()
    {
        var (gateway, console) = Gateway(Linked(), Console);

        var devices = await gateway.ListDevicesAsync(Ct);

        var token = console.Calls.First();
        token.Request.RequestUri!.ToString().ShouldBe("https://s123.mobicontrolcloud.com/mobicontrol/api/token");
        token.Request.Headers.Authorization!.Scheme.ShouldBe("Basic");
        token.Body!.ShouldContain("grant_type=password");
        console.Calls.Last().Request.Headers.Authorization!.Parameter.ShouldBe("tok-1");

        devices.Count.ShouldBe(2);
        devices[0].ShouldSatisfyAllConditions(
            d => d.Model.ShouldBe("Zebra TC26"),
            d => d.Serial.ShouldBe("R58N"),
            d => d.Online.ShouldBeTrue(),
            d => d.BatteryPercent.ShouldBe(81),
            d => d.LastSeenAt.ShouldBe(new DateTimeOffset(2026, 11, 9, 14, 0, 0, TimeSpan.Zero)),
            d => d.EnrolledAt.ShouldNotBeNull());
        devices[1].ShouldSatisfyAllConditions(
            d => d.Model.ShouldBe("Equipo"),
            d => d.Serial.ShouldBe("dev-2"),
            d => d.BatteryPercent.ShouldBeNull(),
            d => d.EnrolledAt.ShouldBeNull());
    }

    [Fact]
    public async Task La_verificacion_reporta_la_consola_pero_no_pasa_mientras_no_haya_ordenes_de_jornada()
    {
        var (gateway, _) = Gateway(Linked(), Console);

        var checks = await gateway.VerifyAsync(Ct);

        checks.Single(c => c.Code == "one_link").Passed.ShouldBeTrue();
        var connection = checks.Single(c => c.Code == "connection");
        connection.Passed.ShouldBeTrue();
        connection.Detail.ShouldContain("2 equipo(s)");
        checks.Single(c => c.Code == "workday_commands").Passed.ShouldBeFalse();
        gateway.CanControlWorkday.ShouldBeFalse();
    }

    [Fact]
    public async Task Sin_vinculo_con_ONE_no_llama_a_la_consola_y_explica_que_falta()
    {
        var (gateway, console) = Gateway(OneConfigurationResult.Fail("WorkLog no tiene la credencial de ONE."), Console);

        (await gateway.ListDevicesAsync(Ct)).ShouldBeEmpty();
        var checks = await gateway.VerifyAsync(Ct);

        console.Calls.ShouldBeEmpty();
        checks.Single(c => c.Code == "one_link").Detail.ShouldContain("credencial de ONE");
        checks.ShouldAllBe(c => !c.Passed);
    }

    [Fact]
    public async Task Si_la_consola_rechaza_el_usuario_lo_dice_sin_romper_Equipo_hoy()
    {
        var (gateway, _) = Gateway(Linked(), _ => new HttpResponseMessage(HttpStatusCode.BadRequest));

        (await gateway.ListDevicesAsync(Ct)).ShouldBeEmpty();
        (await gateway.VerifyAsync(Ct)).Single(c => c.Code == "connection").Detail.ShouldContain("rechazó el ingreso (400)");
    }

    private static OneIntegrationClient OneClient(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(new HttpClient(new StubHttp(_ => StubHttp.Json(json, status))) { BaseAddress = new Uri("https://one.test/") },
            Options.Create(new OneOptions { BaseUrl = "https://one.test/", AppSlug = "worklog" }),
            NullLogger<OneIntegrationClient>.Instance);

    private static string OneResponse(Guid tenantId, string appSlug) => $$"""
        {"tenant":{"id":"{{tenantId}}","name":"INTECHSYS","slug":"intechsys"},
         "app":{"id":"{{Guid.CreateVersion7()}}","name":"App","slug":"{{appSlug}}"},
         "settings":{"MOBICONTROL_BASE_URL":"https://s1.mobicontrolcloud.com/mobicontrol"},
         "configVersion":"abc"}
        """;

    [Fact]
    public async Task ONE_solo_acepta_credenciales_de_la_misma_empresa_y_de_WorkLog()
    {
        var tenant = Guid.CreateVersion7();

        var ok = await OneClient(OneResponse(tenant, "worklog")).GetConfigurationAsync("key", "secret", tenant, Ct);
        ok.Configuration.ShouldNotBeNull().Settings[MobiControlSettings.BaseUrl].ShouldBe("https://s1.mobicontrolcloud.com/mobicontrol");

        (await OneClient(OneResponse(Guid.CreateVersion7(), "worklog")).GetConfigurationAsync("key", "secret", tenant, Ct))
            .Problem.ShouldNotBeNull().ShouldContain("no de esta");
        (await OneClient(OneResponse(tenant, "geomonitoreo")).GetConfigurationAsync("key", "secret", tenant, Ct))
            .Problem.ShouldNotBeNull().ShouldContain("no de WorkLog");
        (await OneClient("""{"title":"Credenciales inválidas."}""", HttpStatusCode.Unauthorized).GetConfigurationAsync("key", "secret", tenant, Ct))
            .Configuration.ShouldBeNull();
    }
}
