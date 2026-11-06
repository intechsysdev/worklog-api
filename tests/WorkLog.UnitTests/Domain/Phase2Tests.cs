using WorkLog.Domain.Activities;
using WorkLog.Domain.Devices;
using WorkLog.Domain.Geo;
using WorkLog.Domain.Hours;
using WorkLog.Domain.Payroll;
using WorkLog.Domain.Requests;
using WorkLog.Domain.Scheduling;
using WorkLog.Domain.Workdays;

namespace WorkLog.UnitTests.Domain;

public sealed class HoursCalculatorTests
{
    private static readonly TimeZoneInfo Bogota = TimeZoneInfo.FindSystemTimeZoneById("America/Bogota");
    private static readonly HourRules Colombia = new(42 * 60, new TimeOnly(19, 0), new TimeOnly(6, 0));

    private static DateTimeOffset At(DateOnly day, int hour, int minute = 0) =>
        new(day.Year, day.Month, day.Day, hour, minute, 0, TimeSpan.FromHours(-5));

    private static DayWork Day(DateOnly date, int from, int to, int? scheduled) =>
        new(date, [(At(date, from), At(date, to))], scheduled);

    [Fact]
    public void Lo_que_pasa_del_turno_es_extra_y_lo_que_pasa_de_las_19_es_nocturno()
    {
        var thursday = new DateOnly(2026, 10, 29);
        var (days, total) = HoursCalculator.Calculate([Day(thursday, 7, 21, 8 * 60)], Bogota, Colombia, new HashSet<DateOnly>());

        total.OrdinaryDay.ShouldBe(8 * 60);
        total.OvertimeDay.ShouldBe(4 * 60);   // 15:00–19:00
        total.OvertimeNight.ShouldBe(2 * 60); // 19:00–21:00
        days[0].WorkedMinutes.ShouldBe(14 * 60);
    }

    [Fact]
    public void El_tope_semanal_convierte_en_extra_aunque_el_dia_este_dentro_del_turno()
    {
        var monday = new DateOnly(2026, 10, 26);
        var work = Enumerable.Range(0, 6).Select(i => Day(monday.AddDays(i), 7, 15, 8 * 60)).ToList();

        var (_, total) = HoursCalculator.Calculate(work, Bogota, Colombia, new HashSet<DateOnly>());

        total.Ordinary.ShouldBe(42 * 60);
        total.Overtime.ShouldBe(6 * 60);
    }

    [Fact]
    public void Domingo_y_festivo_se_cuentan_aparte_y_el_tope_se_reinicia_el_lunes()
    {
        var sunday = new DateOnly(2026, 11, 1);
        var holidayMonday = new DateOnly(2026, 11, 2);

        var (_, total) = HoursCalculator.Calculate(
            [Day(sunday, 8, 12, null), Day(holidayMonday, 8, 10, null)], Bogota, Colombia, new HashSet<DateOnly> { holidayMonday });

        total.SundayHoliday.ShouldBe(6 * 60);
        total.Ordinary.ShouldBe(6 * 60);
    }

    [Fact]
    public void Dia_de_descanso_trabajado_es_todo_extra()
    {
        var saturday = new DateOnly(2026, 10, 31);
        var (_, total) = HoursCalculator.Calculate([Day(saturday, 8, 12, 0)], Bogota, Colombia, new HashSet<DateOnly>());

        total.OvertimeDay.ShouldBe(4 * 60);
        total.Ordinary.ShouldBe(0);
    }
}

public sealed class RequestsAndAdjustmentsTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly DateOnly Day = new(2026, 10, 29);

    private static DateTimeOffset At(int hour, int minute) => new(2026, 10, 29, hour, minute, 0, TimeSpan.FromHours(-5));

    private static Workday ClosedWorkday(out ActivityRecord first, out ActivityRecord second)
    {
        var plan = new DayPlan(Day, DayKind.Shift, new TimeOnly(7, 0), new TimeOnly(16, 0), 60, null, "Campo", null, null, null);
        var workday = Workday.Start(new WorkdayOpening(Tenant, Guid.CreateVersion7(), null, Day, At(7, 3), false, 0, plan, null, GeofenceStatus.NotApplicable, null));
        var snapshot = new ActivitySnapshot(Guid.CreateVersion7(), "Instalación", Guid.CreateVersion7(), "Productivo", TimeClass.Productive, "#AF1839", null, null, null, null);

        first = workday.StartActivity(snapshot, At(8, 12), null, GeofenceStatus.NotApplicable).Value;
        workday.FinishActivity(first.Id, new ActivityFinish(At(9, 40), ActivityOutcome.Completed, null, 0, false, null, GeofenceStatus.NoLocation),
            new FinishRequirements(false, false, false));
        second = workday.StartActivity(snapshot with { Name = "Mantenimiento" }, At(10, 0), null, GeofenceStatus.NotApplicable).Value;
        workday.Close(At(15, 58), null, GeofenceStatus.NoLocation, null, CloseKind.Manual);
        return workday;
    }

    [Fact]
    public void Un_ajuste_valido_corrige_la_hora_y_marca_la_jornada()
    {
        var workday = ClosedWorkday(out var first, out _);

        workday.ApplyAdjustment(AdjustmentTarget.ActivityEnd, first.Id, At(9, 55)).IsSuccess.ShouldBeTrue();

        first.EndedAt.ShouldBe(At(9, 55));
        workday.WasAdjusted.ShouldBeTrue();
    }

    [Fact]
    public void Un_ajuste_no_puede_montar_una_actividad_sobre_otra()
    {
        var workday = ClosedWorkday(out var first, out var second);

        workday.ApplyAdjustment(AdjustmentTarget.ActivityEnd, first.Id, At(10, 30)).Error!.Code.ShouldBe("adjustment.invalid");
        workday.ApplyAdjustment(AdjustmentTarget.ActivityStart, second.Id, At(9, 0)).Error!.Code.ShouldBe("adjustment.invalid");
    }

    [Fact]
    public void El_rechazo_exige_comentario_y_nadie_aprueba_lo_propio()
    {
        var member = Guid.CreateVersion7();
        var request = WorkRequest.OvertimeRequest(Tenant, member, Day, new TimeOnly(16, 0), new TimeOnly(18, 0), "Res. 123", "Cierre de obra", DateTimeOffset.UtcNow).Value;

        request.OvertimeMinutes.ShouldBe(120);
        request.Approve(member, null, DateTimeOffset.UtcNow).Error!.Code.ShouldBe("request.self");
        request.Reject(Guid.CreateVersion7(), "", DateTimeOffset.UtcNow).Error!.Code.ShouldBe("request.comment");
        request.Reject(Guid.CreateVersion7(), "La sede está cerrada", DateTimeOffset.UtcNow).IsSuccess.ShouldBeTrue();
        request.Approve(Guid.CreateVersion7(), null, DateTimeOffset.UtcNow).Error!.Code.ShouldBe("request.resolved");
    }

    [Fact]
    public void Cambio_de_turno_valida_horas_y_dias_repetidos()
    {
        var member = Guid.CreateVersion7();
        WorkRequest.ShiftChangeRequest(Tenant, member, [new ShiftChangeDay(Day, new TimeOnly(12, 0), new TimeOnly(7, 0), 0)], "Cita médica", DateTimeOffset.UtcNow)
            .Error!.Code.ShouldBe("request.day_range");
        WorkRequest.ShiftChangeRequest(Tenant, member, [new ShiftChangeDay(Day, null, null, 0), new ShiftChangeDay(Day, null, null, 0)], "Cita médica", DateTimeOffset.UtcNow)
            .Error!.Code.ShouldBe("request.days_repeated");
    }
}

public sealed class PayrollAndDevicesTests
{
    [Fact]
    public void Quincenas_del_1_al_15_y_del_16_al_fin_de_mes()
    {
        var second = Fortnight.Containing(new DateOnly(2026, 10, 29));
        second.From.ShouldBe(new DateOnly(2026, 10, 16));
        second.To.ShouldBe(new DateOnly(2026, 10, 31));
        second.Previous.ShouldBe(new Fortnight(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 15)));
        Fortnight.Containing(new DateOnly(2027, 2, 20)).To.Day.ShouldBe(28);
    }

    [Fact]
    public void El_periodo_solo_cierra_terminado_y_con_el_checklist_completo()
    {
        var period = PayrollPeriod.Open(Guid.CreateVersion7(), Guid.CreateVersion7(), Fortnight.Containing(new DateOnly(2026, 10, 20)));
        var full = new PayrollChecklist(true, true, true);

        period.Close(Guid.CreateVersion7(), DateTimeOffset.UtcNow, new DateOnly(2026, 10, 31), full, []).Error!.Code.ShouldBe("period.not_finished");
        period.Close(Guid.CreateVersion7(), DateTimeOffset.UtcNow, new DateOnly(2026, 11, 3), full with { ResolvedRequests = false }, []).Error!.Code.ShouldBe("period.checklist");
        period.Close(Guid.CreateVersion7(), DateTimeOffset.UtcNow, new DateOnly(2026, 11, 3), full,
            [new PayrollLineData(Guid.CreateVersion7(), "Andrés Mora", new HoursBreakdown(4800, 0, 120, 0, 0), null)]).IsSuccess.ShouldBeTrue();

        period.IsClosed.ShouldBeTrue();
        period.Lines.Single().OvertimeDay.ShouldBe(120);
    }

    [Fact]
    public void Control_de_dispositivos_no_se_activa_sin_verificacion_ni_alcance()
    {
        var settings = DeviceControlSettings.Default(Guid.CreateVersion7());
        var branch = Guid.CreateVersion7();
        var data = new DeviceControlData(true, [branch], [], [15, 5], true, OfflinePolicy.AllowAndNotify, ["com.empresa.ot"], []);

        settings.Update(data).Error!.Code.ShouldBe("device.not_verified");

        settings.RecordVerification(true, DateTimeOffset.UtcNow);
        settings.Update(data with { BranchIds = [] }).Error!.Code.ShouldBe("device.scope");
        settings.Update(data with { WarningMinutes = [7] }).Error!.Code.ShouldBe("device.warnings");
        settings.Update(data).IsSuccess.ShouldBeTrue();

        settings.AppliesTo(branch, null).ShouldBeTrue();
        settings.AppliesTo(Guid.CreateVersion7(), null).ShouldBeFalse();
        settings.AlwaysAvailableApps.ShouldContain("com.intechsys.worklog");
    }
}
