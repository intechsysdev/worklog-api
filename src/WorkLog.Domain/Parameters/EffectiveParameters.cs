using WorkLog.Domain.Scheduling;

namespace WorkLog.Domain.Parameters;

/// <summary>Valor resuelto de un parámetro y el nivel de donde viene.</summary>
public sealed record ResolvedParameter(ParameterDefinition Definition, int Value, ParameterLevel? Source, DateOnly? EffectiveFrom, string? LegalBasis)
{
    /// <summary>Sin valor guardado en ningún nivel: se usa el de la definición.</summary>
    public bool IsDefault => Source is null;
}

/// <summary>
/// Parámetros efectivos para una sede (o empresa, o país) en una fecha. Gana el nivel más
/// específico; dentro de un nivel, el valor más reciente que ya esté vigente.
/// </summary>
public sealed class EffectiveParameters
{
    private readonly Dictionary<ParameterKey, ResolvedParameter> _values;

    private EffectiveParameters(Dictionary<ParameterKey, ResolvedParameter> values) => _values = values;

    public IReadOnlyCollection<ResolvedParameter> Values => _values.Values;

    public int this[ParameterKey key] => _values[key].Value;

    public ResolvedParameter Get(ParameterKey key) => _values[key];

    /// <param name="chain">Niveles a considerar, del más general al más específico.</param>
    /// <param name="candidates">Valores guardados de esos niveles (pueden venir de más).</param>
    public static EffectiveParameters Resolve(IReadOnlyList<ParameterScope> chain, IEnumerable<ParameterValue> candidates, DateOnly date)
    {
        var applicable = candidates.Where(v => v.EffectiveFrom <= date).ToList();
        var values = new Dictionary<ParameterKey, ResolvedParameter>();

        foreach (var definition in ParameterDefinition.All)
        {
            ResolvedParameter resolved = new(definition, definition.DefaultValue, null, null, null);

            foreach (var scope in chain)
            {
                // Los legales solo se leen del país: un valor legal en otro nivel no debería existir.
                if (definition.IsLegal && scope.Level is ParameterLevel.Company or ParameterLevel.Branch) continue;

                var latest = applicable
                    .Where(v => v.Key == definition.Key && scope.Matches(v))
                    .MaxBy(v => v.EffectiveFrom);

                if (latest is not null)
                    resolved = new ResolvedParameter(definition, latest.Value, scope.Level, latest.EffectiveFrom, latest.LegalBasis);
            }

            values[definition.Key] = resolved;
        }

        return new EffectiveParameters(values);
    }

    /// <summary>Cadena de herencia de una sede: Global → País → Empresa → Sede.</summary>
    public static IReadOnlyList<ParameterScope> ChainFor(string? countryCode, Guid? tenantId, Guid? branchId)
    {
        var chain = new List<ParameterScope> { ParameterScope.Global };
        if (!string.IsNullOrWhiteSpace(countryCode)) chain.Add(ParameterScope.Country(countryCode));
        if (tenantId is { } tenant) chain.Add(ParameterScope.Company(tenant));
        if (tenantId is { } t && branchId is { } branch) chain.Add(ParameterScope.Branch(t, branch));
        return chain;
    }

    public LaborRules ToLaborRules() => new(
        WeeklyCapMinutes: this[ParameterKey.WeeklyCapHours] * 60,
        NightStart: new TimeOnly(this[ParameterKey.NightStartHour], 0));
}
