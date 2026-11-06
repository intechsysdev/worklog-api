using WorkLog.Domain.Common;

namespace WorkLog.Domain.Parameters;

/// <summary>
/// Valor de un parámetro en un nivel, vigente desde una fecha. Global y País son del producto
/// (sin empresa); Empresa y Sede pertenecen a una empresa.
/// </summary>
public sealed class ParameterValue : AuditableEntity
{
    private ParameterValue()
    {
    }

    public ParameterKey Key { get; private set; }
    public ParameterLevel Level { get; private set; }

    /// <summary>Empresa dueña del valor (niveles Empresa y Sede).</summary>
    public Guid? TenantId { get; private set; }

    /// <summary>Código de país (nivel País) o id de la sede (nivel Sede).</summary>
    public string? Scope { get; private set; }

    public int Value { get; private set; }
    public DateOnly EffectiveFrom { get; private set; }

    /// <summary>Norma o soporte del cambio; obligatorio para los parámetros legales.</summary>
    public string? LegalBasis { get; private set; }

    public static Result<ParameterValue> Create(ParameterScope scope, ParameterKey key, int value, DateOnly effectiveFrom, string? legalBasis)
    {
        var definition = ParameterDefinition.Of(key);

        if (!definition.IsEditableAt(scope.Level))
            return DomainError.Validation("parameter.legal_level", "Los parámetros legales se editan en el país.", "level");

        if (!definition.Accepts(value))
        {
            return DomainError.Validation("parameter.range",
                $"El valor debe estar entre {definition.Min} y {definition.Max}, en pasos de {definition.Step}.", "value");
        }

        var basis = Text.CleanOrNull(legalBasis);
        if (definition.IsLegal && (basis is null || basis.Length < 5))
            return DomainError.Validation("parameter.legal_basis", "Escribe la norma o el soporte del cambio.", "legalBasis");

        if (Guard.MaxLength(basis, "legalBasis", 300, "La norma") is { } basisError) return basisError;

        return new ParameterValue
        {
            Key = key,
            Level = scope.Level,
            TenantId = scope.TenantId,
            Scope = scope.Scope,
            Value = value,
            EffectiveFrom = effectiveFrom,
            LegalBasis = basis
        };
    }
}

/// <summary>Dónde se guarda un valor: nivel más empresa y país/sede según corresponda.</summary>
public sealed record ParameterScope(ParameterLevel Level, Guid? TenantId, string? Scope)
{
    public static ParameterScope Global { get; } = new(ParameterLevel.Global, null, null);

    public static ParameterScope Country(string countryCode) => new(ParameterLevel.Country, null, countryCode.ToUpperInvariant());

    public static ParameterScope Company(Guid tenantId) => new(ParameterLevel.Company, tenantId, null);

    public static ParameterScope Branch(Guid tenantId, Guid branchId) => new(ParameterLevel.Branch, tenantId, branchId.ToString());

    public bool Matches(ParameterValue value) =>
        value.Level == Level && value.TenantId == TenantId && string.Equals(value.Scope, Scope, StringComparison.OrdinalIgnoreCase);
}
