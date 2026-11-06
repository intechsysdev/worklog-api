using WorkLog.Domain.Absences;
using WorkLog.Domain.Scheduling;

namespace WorkLog.UnitTests.Domain;

public sealed class SchedulingTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly LaborRules Colombia = new(42 * 60, new TimeOnly(19, 0));

    private static ScheduleTemplate Template(string name, WeekDays days, int start, int end, int lunch) =>
        ScheduleTemplate.Create(Tenant, new ScheduleTemplateData(name, days, new TimeOnly(start, 0), new TimeOnly(end, 0), lunch), Colombia).Value;

    [Fact]
    public void Template_de_40_horas_cumple()
    {
        var template = Template("Campo L-V", WeekDays.MondayToFriday, 7, 16, 60);

        template.WeeklyMinutes.ShouldBe(40 * 60);
        template.IsCompliant(Colombia).ShouldBeTrue();
        template.Summary.ShouldBe("LMXJV · 07:00–16:00 · 40 h/sem");
    }

    [Fact]
    public void Template_que_pasa_el_tope_semanal_no_se_crea()
    {
        var result = ScheduleTemplate.Create(Tenant,
            new ScheduleTemplateData("Larga", WeekDays.MondayToSaturday, new TimeOnly(7, 0), new TimeOnly(15, 30), 30), Colombia);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe("template.weekly_cap");
    }

    [Fact]
    public void Template_con_domingo_advierte_pero_no_bloquea()
    {
        var checks = ScheduleTemplate.Evaluate(WeekDays.Monday | WeekDays.Tuesday | WeekDays.Wednesday | WeekDays.Thursday | WeekDays.Sunday,
            new TimeOnly(8, 0), new TimeOnly(16, 0), 0, Colombia);

        checks.ShouldContain(c => c.Code == "sunday" && c.Severity == CheckSeverity.Warning);
        checks.ShouldNotContain(c => c.Severity == CheckSeverity.Fail);
    }

    [Fact]
    public void Gana_la_asignacion_mas_especifica_y_la_mas_reciente()
    {
        var branchId = Guid.CreateVersion7();
        var groupId = Guid.CreateVersion7();
        var memberId = Guid.CreateVersion7();
        var campo = Template("Campo", WeekDays.MondayToFriday, 7, 16, 60);
        var oficina = Template("Oficina", WeekDays.MondayToFriday, 8, 17, 60);
        var media = Template("Media", WeekDays.MondayToSaturday, 7, 11, 0);

        var assignments = new[]
        {
            ScheduleAssignment.Create(Tenant, AssignmentLevel.Branch, branchId, campo.Id, new DateOnly(2026, 1, 1)).Value,
            ScheduleAssignment.Create(Tenant, AssignmentLevel.Group, groupId, oficina.Id, new DateOnly(2026, 6, 1)).Value,
            ScheduleAssignment.Create(Tenant, AssignmentLevel.Member, memberId, media.Id, new DateOnly(2026, 12, 1)).Value
        };

        var context = new ScheduleContext(memberId, groupId, branchId, assignments,
            new[] { campo, oficina, media }.ToDictionary(t => t.Id), [], [], []);

        ShiftResolver.Resolve(new DateOnly(2026, 3, 2), context).TemplateName.ShouldBe("Campo");
        ShiftResolver.Resolve(new DateOnly(2026, 11, 3), context).TemplateName.ShouldBe("Oficina");
        ShiftResolver.Resolve(new DateOnly(2026, 12, 1), context).TemplateName.ShouldBe("Media");
    }

    [Fact]
    public void Festivo_y_novedad_quitan_el_turno_y_el_dia_fuera_de_plantilla_es_descanso()
    {
        var branchId = Guid.CreateVersion7();
        var memberId = Guid.CreateVersion7();
        var campo = Template("Campo", WeekDays.MondayToFriday, 7, 16, 60);
        var assignment = ScheduleAssignment.Create(Tenant, AssignmentLevel.Branch, branchId, campo.Id, new DateOnly(2026, 1, 1)).Value;

        var vacation = Absence.Register(Tenant, memberId, AbsenceType.Vacation, new DateOnly(2026, 11, 3), new DateOnly(2026, 11, 13), null, null, null).Value;
        vacation.Approve(Guid.CreateVersion7(), DateTimeOffset.UtcNow);

        var context = new ScheduleContext(memberId, null, branchId, [assignment], new Dictionary<Guid, ScheduleTemplate> { [campo.Id] = campo },
            [], [vacation], [Holiday.Create("CO", new DateOnly(2026, 11, 2), "Todos los Santos")]);

        ShiftResolver.Resolve(new DateOnly(2026, 11, 2), context).Kind.ShouldBe(DayKind.Holiday);
        ShiftResolver.Resolve(new DateOnly(2026, 11, 4), context).Kind.ShouldBe(DayKind.Absence);
        ShiftResolver.Resolve(new DateOnly(2026, 11, 7), context).Kind.ShouldBe(DayKind.Rest);
        ShiftResolver.Resolve(new DateOnly(2026, 11, 16), context).Kind.ShouldBe(DayKind.Shift);
    }

    [Fact]
    public void Cambio_puntual_reemplaza_el_turno_del_dia()
    {
        var memberId = Guid.CreateVersion7();
        var campo = Template("Campo", WeekDays.MondayToFriday, 7, 16, 60);
        var assignment = ScheduleAssignment.Create(Tenant, AssignmentLevel.Member, memberId, campo.Id, new DateOnly(2026, 1, 1)).Value;
        var change = ShiftOverride.Create(Tenant, memberId, new DateOnly(2026, 10, 30), new TimeOnly(7, 0), new TimeOnly(12, 0), 0, "Cita médica", null).Value;

        var plan = ShiftResolver.Resolve(new DateOnly(2026, 10, 30),
            new ScheduleContext(memberId, null, null, [assignment], new Dictionary<Guid, ScheduleTemplate> { [campo.Id] = campo }, [change], [], []));

        plan.Kind.ShouldBe(DayKind.Override);
        plan.ScheduledMinutes.ShouldBe(5 * 60);
    }
}
