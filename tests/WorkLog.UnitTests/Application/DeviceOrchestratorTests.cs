using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using WorkLog.Application.Abstractions;
using WorkLog.Application.Devices;
using WorkLog.Domain.Access;
using WorkLog.Domain.Devices;
using WorkLog.Infrastructure;

namespace WorkLog.UnitTests.Application;

public sealed class DeviceOrchestratorTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly Guid Branch = Guid.CreateVersion7();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly IDeviceManagementGateway _gateway = Substitute.For<IDeviceManagementGateway>();
    private readonly IDeviceCommandRepository _commands = Substitute.For<IDeviceCommandRepository>();

    /// <summary>Control encendido para la sede, con política Block: el caso que más daño haría sin consola.</summary>
    private DeviceOrchestrator Orchestrator()
    {
        var settings = DeviceControlSettings.Default(Tenant);
        settings.RecordVerification(true, DateTimeOffset.UtcNow);
        settings.Update(new DeviceControlData(true, [Branch], [], [15], true, OfflinePolicy.Block, [], [])).IsSuccess.ShouldBeTrue();

        var repository = Substitute.For<IDeviceControlRepository>();
        repository.GetAsync(Arg.Any<CancellationToken>()).Returns(settings);
        var tenant = Substitute.For<ITenantContext>();
        tenant.TenantId.Returns(Tenant);

        return new DeviceOrchestrator(tenant, repository, _commands, _gateway, Substitute.For<IAuditTrail>(), TimeProvider.System);
    }

    private static Member Technician()
    {
        var member = Member.Invite(Tenant, new MemberProfile("Andres Mora", "amora@intechsys.co", MemberUsage.Mobile,
            Guid.CreateVersion7(), Branch, null, null)).Value;
        member.AssignDevice("mc-r01", "Zebra TC26").IsSuccess.ShouldBeTrue();
        return member;
    }

    [Fact]
    public async Task Sin_consola_conectada_la_jornada_no_espera_a_MobiControl_aunque_lo_guardado_diga_encendido()
    {
        _gateway.CanControlWorkday.Returns(false);
        var orchestrator = Orchestrator();
        var member = Technician();

        (await orchestrator.ControlsAsync(member, Ct)).ShouldBeFalse();
        (await orchestrator.OnWorkdayOpeningAsync(member, Ct)).ShouldBeTrue();
        await orchestrator.OnWorkdayClosedAsync(member, Ct);

        await _gateway.DidNotReceiveWithAnyArgs().SetWorkdayStateAsync(default!, default, Ct);
        _commands.DidNotReceiveWithAnyArgs().Add(default!);
    }

    [Fact]
    public async Task Con_consola_conectada_y_politica_Block_no_deja_iniciar_si_MobiControl_no_responde()
    {
        _gateway.CanControlWorkday.Returns(true);
        _gateway.SetWorkdayStateAsync("mc-r01", true, Arg.Any<CancellationToken>())
            .Returns(new GatewayResult(DeviceCommandStatus.Failed, "Sin respuesta."));
        var orchestrator = Orchestrator();
        var member = Technician();

        (await orchestrator.ControlsAsync(member, Ct)).ShouldBeTrue();
        (await orchestrator.OnWorkdayOpeningAsync(member, Ct)).ShouldBeFalse();
        _commands.ReceivedWithAnyArgs(1).Add(default!);
    }

    [Theory]
    [InlineData(null, "DisconnectedMobiControlGateway")]
    [InlineData("Disabled", "DisconnectedMobiControlGateway")]
    [InlineData("Rest", "MobiControlRestGateway")]
    [InlineData("Otro", "DisconnectedMobiControlGateway")]
    [InlineData("simulator", "SimulatedMobiControlGateway")]
    public void El_simulador_solo_se_usa_si_se_pide_explicitamente(string? mode, string expected)
    {
        var values = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = "Server=.;Database=WorkLog;Trusted_Connection=True",
            ["MobiControl:Mode"] = mode
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        var services = new ServiceCollection().AddWorkLogInfrastructure(configuration);

        services.Single(d => d.ServiceType == typeof(IDeviceManagementGateway)).ImplementationType!.Name.ShouldBe(expected);
    }
}
