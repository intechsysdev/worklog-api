using WorkLog.Domain.Activities;
using WorkLog.Domain.Geo;
using WorkLog.Domain.Scheduling;
using WorkLog.Domain.Workdays;

namespace WorkLog.UnitTests.Domain;

public sealed class WorkdayTests
{
    private static readonly TimeZoneInfo Bogota = TimeZoneInfo.FindSystemTimeZoneById("America/Bogota");
    private static readonly DateOnly Day = new(2026, 10, 29);

    private static readonly DayPlan Shift = new(Day, DayKind.Shift, new TimeOnly(7, 0), new TimeOnly(16, 0), 60,
        Guid.CreateVersion7(), "Campo", AssignmentLevel.Branch, Guid.CreateVersion7(), "Campo");

    private static DateTimeOffset At(int hour, int minute) => new(2026, 10, 29, hour, minute, 0, TimeSpan.FromHours(-5));

    private static ActivitySnapshot Snapshot(string name, TimeClass timeClass = TimeClass.Productive) =>
        new(Guid.CreateVersion7(), name, Guid.CreateVersion7(), "Productivo", timeClass, "#AF1839", null, null, null, null);

    private static Workday Open(DateTimeOffset at) =>
        Workday.Start(new WorkdayOpening(Guid.CreateVersion7(), Guid.CreateVersion7(), null, Day, at, false, 0, Shift, null,
            GeofenceStatus.NotApplicable, null));

    [Fact]
    public void No_deja_iniciar_antes_de_la_ventana_pero_corrige_un_inicio_encolado()
    {
        var tooEarly = WorkdayPolicy.EvaluateStart(Shift, At(6, 41), Bogota, earlyStartMinutes: 15, lateToleranceMinutes: 5, queuedOffline: false);
        tooEarly.IsFailure.ShouldBeTrue();
        tooEarly.Error!.Message.ShouldBe("Tu turno empieza a las 07:00. Puedes iniciar desde las 06:45.");

        var queued = WorkdayPolicy.EvaluateStart(Shift, At(6, 41), Bogota, 15, 5, queuedOffline: true);
        queued.Value.Adjusted.ShouldBeTrue();
        queued.Value.StartAt.ShouldBe(At(6, 45));
    }

    [Fact]
    public void Marca_tarde_despues_de_la_tolerancia()
    {
        WorkdayPolicy.EvaluateStart(Shift, At(7, 4), Bogota, 15, 5, false).Value.LateMinutes.ShouldBe(0);
        WorkdayPolicy.EvaluateStart(Shift, At(7, 20), Bogota, 15, 5, false).Value.LateMinutes.ShouldBe(20);
    }

    [Fact]
    public void Sin_turno_no_hay_jornada()
    {
        var holiday = Shift with { Kind = DayKind.Holiday, Start = null, End = null, Label = "Todos los Santos" };
        var result = WorkdayPolicy.EvaluateStart(holiday, At(7, 0), Bogota, 15, 5, false);

        result.Error!.Code.ShouldBe("workday.no_shift");
    }

    [Fact]
    public void Iniciar_otra_actividad_finaliza_la_anterior_a_la_misma_hora()
    {
        var workday = Open(At(7, 3));
        var first = workday.StartActivity(Snapshot("Instalación"), At(8, 12), null, GeofenceStatus.NotApplicable).Value;
        workday.StartActivity(Snapshot("Mantenimiento"), At(9, 40), null, GeofenceStatus.NotApplicable);

        first.Status.ShouldBe(ActivityStatus.Finished);
        first.AutoFinished.ShouldBeTrue();
        first.EndedAt.ShouldBe(At(9, 40));
        workday.CurrentActivity!.Name.ShouldBe("Mantenimiento");
    }

    [Fact]
    public void No_acepta_eventos_anteriores_al_ultimo_registrado()
    {
        var workday = Open(At(7, 3));
        workday.StartActivity(Snapshot("Instalación"), At(8, 12), null, GeofenceStatus.NotApplicable);

        var result = workday.StartActivity(Snapshot("Traslado"), At(8, 0), null, GeofenceStatus.NotApplicable);

        result.Error!.Code.ShouldBe("workday.out_of_order");
    }

    [Fact]
    public void Finalizar_exige_lo_que_pide_la_actividad()
    {
        var workday = Open(At(7, 3));
        var activity = workday.StartActivity(Snapshot("Instalación"), At(8, 12), null, GeofenceStatus.NotApplicable).Value;
        var requirements = new FinishRequirements(Photo: true, Signature: true, Observation: false);

        var withoutSignature = workday.FinishActivity(activity.Id,
            new ActivityFinish(At(9, 40), ActivityOutcome.Completed, null, 1, false, null, GeofenceStatus.NoLocation), requirements);
        withoutSignature.Error!.Code.ShouldBe("activity.signature");

        workday.FinishActivity(activity.Id,
            new ActivityFinish(At(9, 40), ActivityOutcome.Completed, null, 1, true, null, GeofenceStatus.NoLocation), requirements).IsSuccess.ShouldBeTrue();
        activity.Outcome.ShouldBe(ActivityOutcome.Completed);
    }

    [Fact]
    public void Cerrar_finaliza_la_actividad_abierta_y_bloquea_nuevos_eventos()
    {
        var workday = Open(At(7, 3));
        var activity = workday.StartActivity(Snapshot("Mantenimiento"), At(9, 45), null, GeofenceStatus.NotApplicable).Value;

        workday.Close(At(15, 58), null, GeofenceStatus.NoLocation, "Sin novedad", CloseKind.Manual).IsSuccess.ShouldBeTrue();

        activity.Outcome.ShouldBe(ActivityOutcome.Completed);
        activity.EndedAt.ShouldBe(At(15, 58));
        workday.Status.ShouldBe(WorkdayStatus.Closed);
        workday.StartActivity(Snapshot("Otra"), At(16, 0), null, GeofenceStatus.NotApplicable).Error!.Code.ShouldBe("workday.closed");
    }

    [Fact]
    public void La_linea_de_tiempo_separa_productivo_traslado_huecos_y_pausas()
    {
        var workday = Open(At(7, 3));
        workday.StartActivity(Snapshot("Traslado", TimeClass.PaidNonProductive), At(7, 5), null, GeofenceStatus.NotApplicable);
        var install = workday.StartActivity(Snapshot("Instalación"), At(8, 5), null, GeofenceStatus.NotApplicable).Value;
        workday.PauseActivity(install.Id, At(8, 30));
        workday.ResumeActivity(install.Id, At(8, 40));
        workday.FinishActivity(install.Id, new ActivityFinish(At(9, 40), ActivityOutcome.Completed, null, 0, false, null, GeofenceStatus.NoLocation),
            new FinishRequirements(false, false, false));

        var (segments, summary) = WorkdayTimeline.Build(workday, At(9, 45));

        summary.PaidNonProductiveMinutes.ShouldBe(60);
        summary.ProductiveMinutes.ShouldBe(85);
        summary.PausedMinutes.ShouldBe(10);
        summary.IdleMinutes.ShouldBe(2 + 5);
        segments.Count(s => s.Kind == SegmentKind.Idle).ShouldBe(2);
    }

    [Fact]
    public void Cada_evento_sube_la_version_para_el_control_de_concurrencia()
    {
        var workday = Open(At(7, 3));
        var activity = workday.StartActivity(Snapshot("Instalación"), At(8, 0), null, GeofenceStatus.NotApplicable).Value;
        workday.PauseActivity(activity.Id, At(8, 10));

        workday.Version.ShouldBe(2);
    }
}
