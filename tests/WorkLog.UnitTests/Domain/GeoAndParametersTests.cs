using WorkLog.Domain.Access;
using WorkLog.Domain.Geo;
using WorkLog.Domain.Parameters;
using WorkLog.Infrastructure.Persistence.Seed;

namespace WorkLog.UnitTests.Domain;

public sealed class GeoAndParametersTests
{
    private static GeoPoint P(double lat, double lng) => GeoPoint.Create(lat, lng).Value;

    [Fact]
    public void Circulo_valida_dentro_fuera_y_precision_insuficiente()
    {
        var fence = Geofence.Circle(P(4.6870, -74.0480), 120).Value;
        var now = DateTimeOffset.UtcNow;

        fence.Evaluate(new LocationReading(P(4.6875, -74.0482), 6, now), 50).ShouldBe(GeofenceStatus.Inside);
        fence.Evaluate(new LocationReading(P(4.6900, -74.0480), 6, now), 50).ShouldBe(GeofenceStatus.Outside);
        fence.Evaluate(new LocationReading(P(4.6875, -74.0482), 80, now), 50).ShouldBe(GeofenceStatus.Unknown);
        fence.Evaluate(null, 50).ShouldBe(GeofenceStatus.NoLocation);
    }

    [Fact]
    public void Poligono_se_guarda_y_se_restaura_igual()
    {
        var fence = Geofence.Polygon([P(4.0, -74.0), P(4.0, -73.99), P(4.01, -73.99), P(4.01, -74.0)]).Value;
        var restored = Geofence.Restore(fence.Shape, fence.Center.Latitude, fence.Center.Longitude, fence.RadiusMeters, fence.EncodeVertices());

        restored.Contains(P(4.005, -73.995)).ShouldBeTrue();
        restored.Contains(P(4.02, -73.995)).ShouldBeFalse();
    }

    [Fact]
    public void Radio_fuera_de_rango_se_rechaza()
    {
        Geofence.Circle(P(4.6, -74.0), 10).Error!.Field.ShouldBe("radiusMeters");
    }

    [Fact]
    public void Gana_el_nivel_mas_especifico_y_el_valor_ya_vigente()
    {
        var tenant = Guid.CreateVersion7();
        var branch = Guid.CreateVersion7();
        var chain = EffectiveParameters.ChainFor("CO", tenant, branch);
        var values = new[]
        {
            ParameterValue.Create(ParameterScope.Country("CO"), ParameterKey.WeeklyCapHours, 42, new DateOnly(2026, 7, 15), "Ley 2101 de 2021").Value,
            ParameterValue.Create(ParameterScope.Branch(tenant, branch), ParameterKey.LateToleranceMinutes, 10, new DateOnly(2026, 11, 3), null).Value,
            ParameterValue.Create(ParameterScope.Branch(tenant, branch), ParameterKey.LateToleranceMinutes, 15, new DateOnly(2027, 1, 1), null).Value
        };

        var resolved = EffectiveParameters.Resolve(chain, values, new DateOnly(2026, 11, 10));

        resolved[ParameterKey.WeeklyCapHours].ShouldBe(42);
        resolved.Get(ParameterKey.LateToleranceMinutes).Source.ShouldBe(ParameterLevel.Branch);
        resolved[ParameterKey.LateToleranceMinutes].ShouldBe(10);
        resolved.Get(ParameterKey.EarlyStartMinutes).IsDefault.ShouldBeTrue();
    }

    [Fact]
    public void Parametro_legal_solo_en_el_pais_y_con_norma()
    {
        ParameterValue.Create(ParameterScope.Company(Guid.CreateVersion7()), ParameterKey.WeeklyCapHours, 40, new DateOnly(2027, 1, 1), "x")
            .Error!.Code.ShouldBe("parameter.legal_level");
        ParameterValue.Create(ParameterScope.Country("CO"), ParameterKey.WeeklyCapHours, 40, new DateOnly(2027, 1, 1), null)
            .Error!.Code.ShouldBe("parameter.legal_basis");
    }

    [Fact]
    public void Festivos_de_Colombia_con_traslado_y_dias_compartidos()
    {
        var y2026 = ColombianHolidays.For(2026).ToDictionary(h => h.Date, h => h.Name);
        y2026[new DateOnly(2026, 11, 2)].ShouldBe("Todos los Santos");
        y2026[new DateOnly(2026, 11, 16)].ShouldBe("Independencia de Cartagena");
        y2026[new DateOnly(2026, 4, 3)].ShouldBe("Viernes Santo");

        var y2025 = ColombianHolidays.For(2025).ToDictionary(h => h.Date, h => h.Name);
        y2025[new DateOnly(2025, 6, 30)].ShouldBe("Sagrado Corazón y San Pedro y San Pablo");
    }

    [Fact]
    public void Administrador_tiene_todo_y_no_se_limita()
    {
        var admin = DefaultRoles.For(Guid.CreateVersion7()).Single(r => r.IsAdministrator);

        admin.Has(Permission.ViewAudit).ShouldBeTrue();
        admin.SetPermissions(Permission.ViewOwn).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void Un_usuario_movil_no_puede_tener_rol_de_nomina()
    {
        var payroll = DefaultRoles.For(Guid.CreateVersion7()).Single(r => r.SystemKey == SystemRoles.Payroll);

        MemberRules.ValidateRole(MemberUsage.Mobile, payroll).ShouldNotBeNull();
        MemberRules.ValidateRole(MemberUsage.WebOnly, payroll).ShouldBeNull();
    }
}
