using NSubstitute;
using WorkLog.Application.Abstractions;
using WorkLog.Application.Access;
using WorkLog.Application.Common;
using WorkLog.Domain.Access;
using WorkLog.Infrastructure.Security;

namespace WorkLog.UnitTests.Application;

public sealed class MobileAuthServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly Guid _tenant = Guid.CreateVersion7();
    private readonly IPasswordHasher _hasher = new IdentityPasswordHasher();
    private readonly IMobileLoginDirectory _directory = Substitute.For<IMobileLoginDirectory>();
    private readonly IMobileSessionRepository _sessions = Substitute.For<IMobileSessionRepository>();
    private readonly ITenantConfiguration _configuration = Substitute.For<ITenantConfiguration>();
    private readonly List<MobileSession> _added = [];
    private readonly MobileAuthService _service;
    private readonly Member _andres;

    public MobileAuthServiceTests()
    {
        _andres = Mobile(_tenant, "amora@intechsys.co", "temporal1");
        _directory.FindByEmailAsync("amora@intechsys.co", Arg.Any<CancellationToken>()).Returns([new LoginCandidate(_andres, _tenant, "INTECHSYS")]);
        _directory.FindAsync(_tenant, _andres.Id, Arg.Any<CancellationToken>()).Returns(new LoginCandidate(_andres, _tenant, "INTECHSYS"));
        _configuration.ForTenantAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(OneConfigurationResult.Fail("Sin vínculo."));
        _sessions.When(s => s.Add(Arg.Any<MobileSession>())).Do(call => _added.Add(call.Arg<MobileSession>()));

        _service = new MobileAuthService(_directory, _sessions, _hasher, _configuration, Substitute.For<IAuditTrail>(),
            Substitute.For<IUnitOfWork>(), TimeProvider.System);
    }

    private Member Mobile(Guid tenant, string email, string password)
    {
        var member = Member.Invite(tenant, new MemberProfile("Andres Mora", email, MemberUsage.Mobile, Guid.CreateVersion7(),
            Guid.CreateVersion7(), null, null)).Value;
        member.SetTemporaryPassword(_hasher.Hash(password), DateTimeOffset.UtcNow);
        return member;
    }

    private Task<OperationResult<MobileLoginResult>> Login(string password, Guid? tenantId = null) =>
        _service.LoginAsync(new MobileLoginCommand(" AMora@intechsys.co ", password, tenantId, "Zebra TC26"), Ct);

    [Fact]
    public async Task Con_la_contraseña_temporal_entra_activa_la_invitacion_y_pide_cambiarla()
    {
        var result = await Login("temporal1");

        var session = result.Value!.Session.ShouldNotBeNull();
        session.AccessToken.ShouldStartWith(MobileTokens.Prefix);
        session.MustChangePassword.ShouldBeTrue();
        session.TenantName.ShouldBe("INTECHSYS");
        _andres.Status.ShouldBe(MemberStatus.Active);

        var stored = _added.ShouldHaveSingleItem();
        stored.AccessTokenHash.ShouldBe(MobileTokens.Hash(session.AccessToken));
        stored.AccessTokenHash.ShouldNotContain(session.AccessToken);
    }

    [Fact]
    public async Task Cinco_intentos_fallidos_bloquean_la_cuenta_aunque_luego_acierte()
    {
        for (var i = 0; i < Member.MaxFailedLogins - 1; i++)
            (await Login("equivocada9")).Kind.ShouldBe(ErrorKind.Unauthorized);

        // El quinto fallo bloquea y ya lo dice; después, ni la contraseña correcta entra.
        (await Login("equivocada9")).Code.ShouldBe("auth.locked");
        var locked = await Login("temporal1");
        locked.Kind.ShouldBe(ErrorKind.Forbidden);
        locked.Code.ShouldBe("auth.locked");
        _added.ShouldBeEmpty();
    }

    [Fact]
    public async Task Un_correo_que_no_existe_responde_igual_que_una_contraseña_equivocada()
    {
        var unknown = await _service.LoginAsync(new MobileLoginCommand("nadie@intechsys.co", "loquesea1", null, null), Ct);
        var wrong = await Login("equivocada9");

        unknown.Kind.ShouldBe(ErrorKind.Unauthorized);
        unknown.Message.ShouldBe(wrong.Message);
    }

    [Fact]
    public async Task Si_la_cuenta_sirve_en_dos_empresas_pide_elegir_y_luego_entra_en_la_elegida()
    {
        var otherTenant = Guid.CreateVersion7();
        var other = Mobile(otherTenant, "amora@intechsys.co", "temporal1");
        _directory.FindByEmailAsync("amora@intechsys.co", Arg.Any<CancellationToken>()).Returns(
        [
            new LoginCandidate(_andres, _tenant, "INTECHSYS"),
            new LoginCandidate(other, otherTenant, "Cliente Dos")
        ]);

        var choose = (await Login("temporal1")).Value!;
        choose.Session.ShouldBeNull();
        choose.Tenants!.Select(t => t.Name).ShouldBe(["INTECHSYS", "Cliente Dos"]);

        var chosen = (await Login("temporal1", otherTenant)).Value!.Session!;
        chosen.TenantId.ShouldBe(otherTenant);
    }

    [Fact]
    public async Task No_entra_si_ONE_dice_que_WorkLog_no_esta_activo_en_la_empresa()
    {
        _configuration.ForTenantAsync(_tenant, Arg.Any<CancellationToken>())
            .Returns(OneConfigurationResult.Fail("Revocada.", licenseInactive: true));

        var result = await Login("temporal1");

        result.Code.ShouldBe("auth.license");
        _added.ShouldBeEmpty();
    }

    [Fact]
    public async Task Renovar_cambia_los_tokens_y_reusar_el_refresh_anterior_cierra_la_sesion()
    {
        var first = (await Login("temporal1")).Value!.Session!;
        var session = _added.Single();
        _sessions.FindByRefreshHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(session);

        var renewed = (await _service.RefreshAsync(new RefreshSessionCommand(first.RefreshToken), Ct)).Value!;
        renewed.RefreshToken.ShouldNotBe(first.RefreshToken);
        session.RefreshTokenHash.ShouldBe(MobileTokens.Hash(renewed.RefreshToken));

        var reused = await _service.RefreshAsync(new RefreshSessionCommand(first.RefreshToken), Ct);
        reused.Kind.ShouldBe(ErrorKind.Unauthorized);
        session.IsRevoked.ShouldBeTrue();
    }

    [Fact]
    public async Task Cambiar_la_contraseña_exige_la_actual_una_nueva_valida_y_cierra_las_demas_sesiones()
    {
        var current = (await Login("temporal1")).Value!.Session!;
        var mine = _added.Single();
        var otherPhone = MobileSession.Start(_tenant, _andres.Id, MobileTokens.Issue(DateTimeOffset.UtcNow).Hashes, null, DateTimeOffset.UtcNow);
        _sessions.ListOpenAsync(_andres.Id, Arg.Any<CancellationToken>()).Returns([mine, otherPhone]);
        var app = new AppSession(mine.Id, _tenant, _andres.Id, _andres.Email, _andres.FullName, current.MustChangePassword);

        (await _service.ChangePasswordAsync(app, new ChangePasswordCommand("otra", "Nueva2026"), Ct)).Code.ShouldBe("password.current");
        (await _service.ChangePasswordAsync(app, new ChangePasswordCommand("temporal1", "corta1"), Ct)).Code.ShouldBe("password.short");
        (await _service.ChangePasswordAsync(app, new ChangePasswordCommand("temporal1", "sololetras"), Ct)).Code.ShouldBe("password.weak");

        (await _service.ChangePasswordAsync(app, new ChangePasswordCommand("temporal1", "Nueva2026"), Ct)).Succeeded.ShouldBeTrue();
        _andres.MustChangePassword.ShouldBeFalse();
        _hasher.Verify(_andres.PasswordHash!, "Nueva2026").ShouldBeTrue();
        otherPhone.IsRevoked.ShouldBeTrue();
        mine.IsRevoked.ShouldBeFalse();
    }

    [Fact]
    public void Solo_los_usuarios_de_la_app_tienen_contraseña_y_pasar_a_web_la_borra()
    {
        var web = Member.Invite(_tenant, new MemberProfile("Laura Gómez", "lgomez@intechsys.co", MemberUsage.WebOnly, Guid.CreateVersion7(),
            null, null, null)).Value;
        web.SetTemporaryPassword("hash", DateTimeOffset.UtcNow).IsFailure.ShouldBeTrue();

        _andres.UpdateProfile(new MemberProfile(_andres.FullName, _andres.Email, MemberUsage.WebOnly, _andres.RoleId, null, null, null));
        _andres.HasAppPassword.ShouldBeFalse();
    }

    [Fact]
    public void Las_contraseñas_temporales_tienen_letras_y_numeros_y_no_se_repiten()
    {
        var passwords = Enumerable.Range(0, 50).Select(_ => MobileTokens.TemporaryPassword()).ToList();
        passwords.ShouldAllBe(p => p.Length == 10 && p.Any(char.IsLetter) && p.Any(char.IsDigit) && PasswordPolicy.Validate(p) == null);
        passwords.Distinct().Count().ShouldBe(50);
    }
}
