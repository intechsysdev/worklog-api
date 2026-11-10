using NSubstitute;
using WorkLog.Application.Abstractions;
using WorkLog.Application.Workdays;
using WorkLog.Domain.Access;
using WorkLog.Domain.Activities;
using WorkLog.Domain.Geo;
using WorkLog.Domain.Organization;
using WorkLog.Domain.Scheduling;
using WorkLog.Domain.Workdays;

namespace WorkLog.UnitTests.Application;

public sealed class ActivityLogServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly DateOnly Day = new(2026, 10, 29);

    private static readonly DayPlan Shift = new(Day, DayKind.Shift, new TimeOnly(7, 0), new TimeOnly(16, 0), 60,
        Guid.CreateVersion7(), "Campo", AssignmentLevel.Branch, Guid.CreateVersion7(), "Campo");

    private static DateTimeOffset At(int hour, int minute) => new(2026, 10, 29, hour, minute, 0, TimeSpan.FromHours(-5));

    private static ActivitySnapshot Snapshot(string name, TimeClass timeClass = TimeClass.Productive) =>
        new(Guid.CreateVersion7(), name, Guid.CreateVersion7(), "Campo", timeClass, "#AF1839", null, null, null, null);

    private readonly IMemberRepository _members = Substitute.For<IMemberRepository>();
    private readonly IWorkdayRepository _workdays = Substitute.For<IWorkdayRepository>();
    private readonly IEvidenceRepository _evidence = Substitute.For<IEvidenceRepository>();
    private readonly ActivityLogService _service;
    private readonly Member _andres;
    private readonly Member _laura;

    public ActivityLogServiceTests()
    {
        _andres = Mobile("Andres Mora", "amora@intechsys.co");
        _laura = Mobile("Laura Gómez", "lgomez@intechsys.co");
        _members.ListAllAsync(Arg.Any<CancellationToken>()).Returns([_andres, _laura]);

        var branches = Substitute.For<IBranchRepository>();
        branches.ListAsync(Arg.Any<CancellationToken>()).Returns([]);
        var outcomes = Substitute.For<IOutcomeSettingRepository>();
        outcomes.ListAsync(Arg.Any<CancellationToken>()).Returns([]);

        var clock = Substitute.For<TimeProvider>();
        clock.GetUtcNow().Returns(At(17, 0));

        _service = new ActivityLogService(_members, branches, _workdays, _evidence, outcomes, Substitute.For<IFileStorage>(),
            Substitute.For<ISpreadsheetWriter>(), clock);
    }

    private static Member Mobile(string name, string email) =>
        Member.Invite(Tenant, new MemberProfile(name, email, MemberUsage.Mobile, Guid.CreateVersion7(), Guid.CreateVersion7(), null, null)).Value;

    /// <summary>Traslado 07:05–08:05, instalación 08:05–09:40 con pausa de 10 min, cierre 15:58.</summary>
    private static Workday DayOf(Member member)
    {
        var workday = Workday.Start(new WorkdayOpening(Tenant, member.Id, null, Day, At(7, 3), false, 0, Shift, null,
            GeofenceStatus.Inside, "Sede norte"));
        workday.StartActivity(Snapshot("Traslado", TimeClass.PaidNonProductive), At(7, 5), null, GeofenceStatus.NotApplicable);
        var install = workday.StartActivity(Snapshot("Instalación"), At(8, 5), null, GeofenceStatus.Inside).Value;
        workday.PauseActivity(install.Id, At(8, 30));
        workday.ResumeActivity(install.Id, At(8, 40));
        workday.FinishActivity(install.Id,
            new ActivityFinish(At(9, 40), ActivityOutcome.Completed, "Equipo instalado", 0, false, null, GeofenceStatus.Inside),
            new FinishRequirements(false, false, false));
        workday.Close(At(15, 58), null, GeofenceStatus.Inside, "Sin novedad", CloseKind.Manual);
        return workday;
    }

    [Fact]
    public async Task No_consulta_rangos_invertidos_ni_de_mas_de_un_mes()
    {
        (await _service.ListAsync(Day, Day.AddDays(-1), null, null, Ct)).Succeeded.ShouldBeFalse();
        var tooLong = await _service.ListAsync(Day, Day.AddDays(ActivityLog.MaxDays), null, null, Ct);
        tooLong.Succeeded.ShouldBeFalse();
        tooLong.Message!.ShouldContain("31 días");
    }

    [Fact]
    public async Task Lista_las_jornadas_de_la_persona_con_sus_totales()
    {
        var mine = DayOf(_andres);
        _workdays.ListAsync(Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 1 && ids.Contains(_andres.Id)), Day, Day, Arg.Any<CancellationToken>())
            .Returns([mine]);

        var result = await _service.ListAsync(Day, Day, _andres.Id, null, Ct);

        var row = result.Value!.Rows.ShouldHaveSingleItem();
        row.MemberName.ShouldBe("Andres Mora");
        row.Status.ShouldBe(WorkdayStatus.Closed);
        row.ActivityCount.ShouldBe(2);
        row.PausedMinutes.ShouldBe(10);
        result.Value.Totals.People.ShouldBe(1);
        result.Value.Totals.Activities.ShouldBe(2);
    }

    [Fact]
    public async Task El_detalle_trae_cada_actividad_con_pausas_resultado_y_observacion()
    {
        var workday = DayOf(_laura);
        _workdays.FindAsync(workday.Id, Arg.Any<CancellationToken>()).Returns(workday);
        _members.FindAsync(_laura.Id, Arg.Any<CancellationToken>()).Returns(_laura);
        _evidence.ListByWorkdayAsync(workday.Id, Arg.Any<CancellationToken>()).Returns([]);

        var detail = (await _service.GetWorkdayAsync(workday.Id, Ct)).Value!;

        detail.StartSiteName.ShouldBe("Sede norte");
        detail.Note.ShouldBe("Sin novedad");
        detail.Activities.Count.ShouldBe(2);
        var install = detail.Activities[1];
        install.ShouldSatisfyAllConditions(
            a => a.Name.ShouldBe("Instalación"),
            a => a.Minutes.ShouldBe(85),
            a => a.PausedMinutes.ShouldBe(10),
            a => a.Pauses.ShouldHaveSingleItem(),
            a => a.OutcomeLabel.ShouldBe("Completada"),
            a => a.Observation.ShouldBe("Equipo instalado"),
            a => a.Running.ShouldBeFalse());
        detail.Segments.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Una_jornada_inexistente_responde_no_encontrada()
    {
        (await _service.GetWorkdayAsync(Guid.CreateVersion7(), Ct)).Succeeded.ShouldBeFalse();
        (await _service.OpenEvidenceAsync(Guid.CreateVersion7(), Ct)).Succeeded.ShouldBeFalse();
    }
}
