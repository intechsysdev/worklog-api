using WorkLog.Application.Abstractions;
using WorkLog.Application.Common;
using WorkLog.Domain.Organization;
using WorkLog.Domain.Parameters;
using WorkLog.Domain.Scheduling;

namespace WorkLog.Application.Parameters;

/// <summary>Lectura de parámetros efectivos para los casos de uso (no es un puerto de entrada).</summary>
public interface IParameterProvider
{
    Task<EffectiveParameters> ForBranchAsync(Branch? branch, DateOnly date, CancellationToken ct = default);

    Task<EffectiveParameters> ForCompanyAsync(DateOnly date, CancellationToken ct = default);

    /// <summary>Reglas laborales del país de la empresa, para validar plantillas.</summary>
    Task<LaborRules> LaborRulesAsync(DateOnly date, CancellationToken ct = default);
}

internal sealed class ParameterProvider(ITenantContext tenant, ICompanyRepository companies, IParameterRepository values) : IParameterProvider
{
    public async Task<EffectiveParameters> ForBranchAsync(Branch? branch, DateOnly date, CancellationToken ct = default)
    {
        if (branch is null) return await ForCompanyAsync(date, ct);

        var chain = EffectiveParameters.ChainFor(branch.CountryCode, branch.TenantId, branch.Id);
        return EffectiveParameters.Resolve(chain, await values.ListAsync(chain, ct), date);
    }

    public async Task<EffectiveParameters> ForCompanyAsync(DateOnly date, CancellationToken ct = default)
    {
        var tenantId = tenant.TenantId;
        var country = tenantId is { } id ? (await companies.FindAsync(id, ct))?.CountryCode : null;
        var chain = EffectiveParameters.ChainFor(country ?? Company.DefaultCountry, tenantId, null);
        return EffectiveParameters.Resolve(chain, await values.ListAsync(chain, ct), date);
    }

    public async Task<LaborRules> LaborRulesAsync(DateOnly date, CancellationToken ct = default) =>
        (await ForCompanyAsync(date, ct)).ToLaborRules();
}

public sealed record ParameterRowDto(
    string Code,
    string Group,
    string Name,
    string Help,
    string Unit,
    int Min,
    int Max,
    int Step,
    bool IsLegal,
    int Value,
    ParameterLevel? Source,
    DateOnly? EffectiveFrom,
    string? LegalBasis,
    bool Editable,
    bool DefinedHere);

public sealed record SetParameterCommand(int Value, DateOnly? EffectiveFrom, string? LegalBasis);

public interface IParameterService
{
    /// <param name="scope">Código de país (nivel País) o id de la sede (nivel Sede).</param>
    Task<OperationResult<IReadOnlyList<ParameterRowDto>>> GetAsync(ParameterLevel level, string? scope, CancellationToken ct = default);

    Task<OperationResult<IReadOnlyList<ParameterRowDto>>> SetAsync(ParameterLevel level, string? scope, string code, SetParameterCommand command, CancellationToken ct = default);

    /// <summary>"Usar el heredado": borra los valores de ese nivel para el parámetro.</summary>
    Task<OperationResult<IReadOnlyList<ParameterRowDto>>> ResetAsync(ParameterLevel level, string? scope, string code, CancellationToken ct = default);
}

internal sealed class ParameterService(
    ICurrentActor actor,
    ICompanyRepository companies,
    IBranchRepository branches,
    IParameterRepository values,
    IAuditTrail audit,
    IUnitOfWork unitOfWork,
    TimeProvider clock) : IParameterService
{
    public async Task<OperationResult<IReadOnlyList<ParameterRowDto>>> GetAsync(ParameterLevel level, string? scope, CancellationToken ct = default)
    {
        var target = await ResolveScopeAsync(level, scope, ct);
        if (!target.Succeeded) return OperationResult<IReadOnlyList<ParameterRowDto>>.From(target);

        return await RowsAsync(target.Value!, ct);
    }

    public async Task<OperationResult<IReadOnlyList<ParameterRowDto>>> SetAsync(ParameterLevel level, string? scope, string code, SetParameterCommand command, CancellationToken ct = default)
    {
        var target = await ResolveScopeAsync(level, scope, ct);
        if (!target.Succeeded) return OperationResult<IReadOnlyList<ParameterRowDto>>.From(target);

        if (!CanEdit(level))
            return OperationResult<IReadOnlyList<ParameterRowDto>>.Forbidden("Solo la plataforma edita los parámetros globales y de país.");

        if (ParameterDefinition.FromCode(code) is not { } definition)
            return OperationResult<IReadOnlyList<ParameterRowDto>>.NotFound("El parámetro no existe.");

        var today = Today();
        var from = command.EffectiveFrom ?? today;
        if (from < today)
            return OperationResult<IReadOnlyList<ParameterRowDto>>.Invalid("La fecha de aplicación no puede ser pasada: las horas ya calculadas no se recalculan.", "effectiveFrom");

        var created = ParameterValue.Create(target.Value!.Scope, definition.Key, command.Value, from, command.LegalBasis);
        if (created.IsFailure) return OperationResult<IReadOnlyList<ParameterRowDto>>.From(created.Error!);

        var existing = await values.ListAtScopeAsync(target.Value.Scope, definition.Key, ct);
        foreach (var same in existing.Where(v => v.EffectiveFrom == from)) values.Remove(same);
        var previous = existing.Where(v => v.EffectiveFrom <= today).MaxBy(v => v.EffectiveFrom)?.Value;

        values.Add(created.Value);
        audit.Record("parameter.changed",
            $"Cambió «{definition.Name}» en {target.Value.Label}: {previous?.ToString() ?? "heredado"} → {command.Value} {definition.Unit}, desde el {from:dd/MM/yyyy}.",
            "Parámetros", $"{definition.Code}@{target.Value.Label}",
            new { Value = previous }, new { command.Value, EffectiveFrom = from, created.Value.LegalBasis });
        await unitOfWork.SaveChangesAsync(ct);

        return await RowsAsync(target.Value, ct);
    }

    public async Task<OperationResult<IReadOnlyList<ParameterRowDto>>> ResetAsync(ParameterLevel level, string? scope, string code, CancellationToken ct = default)
    {
        var target = await ResolveScopeAsync(level, scope, ct);
        if (!target.Succeeded) return OperationResult<IReadOnlyList<ParameterRowDto>>.From(target);

        if (!CanEdit(level))
            return OperationResult<IReadOnlyList<ParameterRowDto>>.Forbidden("Solo la plataforma edita los parámetros globales y de país.");

        if (level == ParameterLevel.Global)
            return OperationResult<IReadOnlyList<ParameterRowDto>>.Invalid("El nivel global no hereda de nadie.");

        if (ParameterDefinition.FromCode(code) is not { } definition)
            return OperationResult<IReadOnlyList<ParameterRowDto>>.NotFound("El parámetro no existe.");

        var existing = await values.ListAtScopeAsync(target.Value!.Scope, definition.Key, ct);
        if (existing.Count > 0)
        {
            foreach (var value in existing) values.Remove(value);
            audit.Record("parameter.reset", $"Volvió a usar el valor heredado de «{definition.Name}» en {target.Value.Label}.",
                "Parámetros", $"{definition.Code}@{target.Value.Label}", new { existing.MaxBy(v => v.EffectiveFrom)!.Value }, null);
            await unitOfWork.SaveChangesAsync(ct);
        }

        return await RowsAsync(target.Value, ct);
    }

    private bool CanEdit(ParameterLevel level) =>
        level is ParameterLevel.Company or ParameterLevel.Branch || actor.IsPlatformAdmin;

    private sealed record Target(ParameterScope Scope, IReadOnlyList<ParameterScope> Chain, string Label);

    private async Task<OperationResult<Target>> ResolveScopeAsync(ParameterLevel level, string? scope, CancellationToken ct)
    {
        var company = await companies.FindAsync(actor.TenantId, ct);
        var country = company?.CountryCode ?? Company.DefaultCountry;

        switch (level)
        {
            case ParameterLevel.Global:
                return new Target(ParameterScope.Global, [ParameterScope.Global], "Global");

            case ParameterLevel.Country:
                var code = (scope ?? country).Trim().ToUpperInvariant();
                if (code.Length != 2) return OperationResult<Target>.Invalid("El país no es válido.", "scope");
                var countryScope = ParameterScope.Country(code);
                return new Target(countryScope, [ParameterScope.Global, countryScope], $"País {code}");

            case ParameterLevel.Company:
                var companyScope = ParameterScope.Company(actor.TenantId);
                return new Target(companyScope, [ParameterScope.Global, ParameterScope.Country(country), companyScope],
                    $"Empresa {company?.Name}");

            case ParameterLevel.Branch:
                if (!Guid.TryParse(scope, out var branchId) || await branches.FindAsync(branchId, ct) is not { } branch)
                    return OperationResult<Target>.NotFound("La sede no existe.");
                var chain = EffectiveParameters.ChainFor(branch.CountryCode, actor.TenantId, branch.Id);
                return new Target(chain[^1], chain, $"Sede {branch.Name}");

            default:
                return OperationResult<Target>.Invalid("Nivel no válido.", "level");
        }
    }

    private async Task<OperationResult<IReadOnlyList<ParameterRowDto>>> RowsAsync(Target target, CancellationToken ct)
    {
        var stored = await values.ListAsync(target.Chain, ct);
        var effective = EffectiveParameters.Resolve(target.Chain, stored, Today());

        IReadOnlyList<ParameterRowDto> rows = ParameterDefinition.All
            // Los legales no se muestran en Global ni en Empresa: se fijan por país.
            .Where(d => !d.IsLegal || target.Scope.Level is ParameterLevel.Country or ParameterLevel.Branch)
            .Select(d =>
            {
                var resolved = effective.Get(d.Key);
                return new ParameterRowDto(d.Code, d.Group, d.Name, d.Help, d.Unit, d.Min, d.Max, d.Step, d.IsLegal,
                    resolved.Value, resolved.Source, resolved.EffectiveFrom, resolved.LegalBasis,
                    d.IsEditableAt(target.Scope.Level) && CanEdit(target.Scope.Level),
                    resolved.Source == target.Scope.Level);
            })
            .ToList();

        return OperationResult<IReadOnlyList<ParameterRowDto>>.Ok(rows);
    }

    private DateOnly Today() => LocalTime.Today(clock, LocalTime.Find(null));
}
