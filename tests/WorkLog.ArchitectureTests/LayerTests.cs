using NetArchTest.Rules;

namespace WorkLog.ArchitectureTests;

/// <summary>
/// Reglas de la arquitectura hexagonal: las dependencias apuntan hacia el dominio. Si alguien
/// usa EF o ASP.NET Core desde el núcleo, la compilación pasa pero estas pruebas fallan.
/// </summary>
public sealed class LayerTests
{
    private static readonly System.Reflection.Assembly Domain = typeof(WorkLog.Domain.Common.Entity).Assembly;
    private static readonly System.Reflection.Assembly Application = typeof(WorkLog.Application.DependencyInjection).Assembly;
    private static readonly System.Reflection.Assembly Infrastructure = typeof(WorkLog.Infrastructure.DependencyInjection).Assembly;

    [Fact]
    public void El_dominio_no_depende_de_nadie()
    {
        var result = Types.InAssembly(Domain).ShouldNot()
            .HaveDependencyOnAny("WorkLog.Application", "WorkLog.Infrastructure", "WorkLog.Api",
                "Microsoft.EntityFrameworkCore", "Microsoft.AspNetCore", "Microsoft.Extensions", "System.Net.Http")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void La_aplicacion_no_conoce_adaptadores_ni_frameworks()
    {
        var result = Types.InAssembly(Application).ShouldNot()
            .HaveDependencyOnAny("WorkLog.Infrastructure", "WorkLog.Api",
                "Microsoft.EntityFrameworkCore", "Microsoft.AspNetCore", "Microsoft.Data.SqlClient", "System.Net.Http")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void La_infraestructura_no_depende_de_la_capa_web()
    {
        var result = Types.InAssembly(Infrastructure).ShouldNot().HaveDependencyOn("WorkLog.Api").GetResult();

        result.IsSuccessful.ShouldBeTrue(string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Las_entidades_no_exponen_setters_publicos()
    {
        var offenders = Domain.GetTypes()
            .Where(t => t.IsClass && typeof(WorkLog.Domain.Common.Entity).IsAssignableFrom(t))
            .SelectMany(t => t.GetProperties().Where(p => p.SetMethod?.IsPublic == true).Select(p => $"{t.Name}.{p.Name}"))
            .ToList();

        offenders.ShouldBeEmpty();
    }

    [Fact]
    public void Los_casos_de_uso_son_internos_y_se_exponen_por_interfaz()
    {
        var result = Types.InAssembly(Application).That().HaveNameEndingWith("Service").And().AreClasses()
            .Should().NotBePublic()
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(string.Join(", ", result.FailingTypeNames ?? []));
    }
}
