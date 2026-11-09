using NSubstitute;
using WorkLog.Application.Abstractions;
using WorkLog.Application.Integrations;
using WorkLog.Domain.Organization;

namespace WorkLog.UnitTests.Application;

public sealed class OneLinkServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly Guid _tenant = Guid.CreateVersion7();
    private readonly Company _company;
    private readonly IOneIntegrationClient _one = Substitute.For<IOneIntegrationClient>();
    private readonly ISecretProtector _protector = Substitute.For<ISecretProtector>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly ITenantConfiguration _configuration = Substitute.For<ITenantConfiguration>();
    private readonly OneLinkService _service;

    public OneLinkServiceTests()
    {
        _company = Company.Register(_tenant, "INTECHSYS", "intechsys");
        var actor = Substitute.For<ICurrentActor>();
        actor.TenantId.Returns(_tenant);
        var companies = Substitute.For<ICompanyRepository>();
        companies.FindAsync(_tenant, Arg.Any<CancellationToken>()).Returns(_company);
        _protector.Protect(Arg.Any<string>()).Returns(call => $"cifrado:{call.Arg<string>()}");

        _service = new OneLinkService(actor, companies, _protector, _one, _configuration, Substitute.For<IAuditTrail>(),
            _unitOfWork, TimeProvider.System);
    }

    [Fact]
    public async Task Una_credencial_que_ONE_rechaza_no_se_guarda()
    {
        _one.GetConfigurationAsync("wl_key_123456", "secreto", _tenant, Arg.Any<CancellationToken>())
            .Returns(OneConfigurationResult.Fail("La credencial es de la empresa «Otra», no de esta."));

        var result = await _service.SaveAsync(new SaveOneLinkCommand("wl_key_123456", "secreto"), Ct);

        result.Succeeded.ShouldBeFalse();
        result.Message!.ShouldContain("no de esta");
        _company.HasOneCredential.ShouldBeFalse();
        await _unitOfWork.DidNotReceiveWithAnyArgs().SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task Al_guardar_el_secreto_queda_cifrado_y_la_respuesta_no_lo_muestra()
    {
        var settings = new Dictionary<string, string?>
        {
            [MobiControlSettings.BaseUrl] = "https://s1.mobicontrolcloud.com/mobicontrol",
            [MobiControlSettings.ClientId] = "c",
            [MobiControlSettings.ClientSecret] = "s"
        };
        _one.GetConfigurationAsync("wl_key_123456", "secreto", _tenant, Arg.Any<CancellationToken>())
            .Returns(new OneConfigurationResult(new OneAppConfiguration(_tenant, "INTECHSYS", settings, "v1"), null));

        var result = await _service.SaveAsync(new SaveOneLinkCommand(" wl_key_123456 ", " secreto "), Ct);

        result.Succeeded.ShouldBeTrue();
        _company.OneApiSecretProtected.ShouldBe("cifrado:secreto");
        var dto = result.Value!;
        dto.ApiKeyHint.ShouldBe("wl_key_1…");
        dto.TenantName.ShouldBe("INTECHSYS");
        dto.MobiControlConfigured.ShouldBeFalse();
        dto.MissingVariables.ShouldBe([MobiControlSettings.User, MobiControlSettings.Password]);
        _configuration.Received(1).Forget(_tenant);
    }
}
