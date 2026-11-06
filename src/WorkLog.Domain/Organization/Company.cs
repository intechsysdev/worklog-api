using WorkLog.Domain.Common;

namespace WorkLog.Domain.Organization;

/// <summary>
/// Copia local de la empresa de INTECHSYS ONE. Su Id es el del tenant en ONE: nadie la crea a
/// mano, aparece la primera vez que entra alguien de esa empresa.
/// </summary>
public sealed class Company : AuditableEntity
{
    public const string DefaultCountry = "CO";

    private Company()
    {
    }

    public string Name { get; private set; } = string.Empty;
    public string? Slug { get; private set; }

    /// <summary>País por defecto: de él salen las reglas laborales con que se validan las plantillas.</summary>
    public string CountryCode { get; private set; } = DefaultCountry;

    /// <summary>Ya se crearon los roles, categorías y resultados con que nace la empresa.</summary>
    public bool IsProvisioned { get; private set; }

    public static Company Register(Guid oneTenantId, string name, string? slug) => new()
    {
        Id = oneTenantId,
        Name = Text.Clean(name).Length == 0 ? "Empresa" : Text.Clean(name),
        Slug = Text.CleanOrNull(slug)
    };

    /// <summary>ONE es la fuente de verdad del nombre: se refresca en cada ingreso.</summary>
    public void Refresh(string name, string? slug)
    {
        if (Text.CleanOrNull(name) is { } cleaned) Name = cleaned;
        Slug = Text.CleanOrNull(slug) ?? Slug;
    }

    public Result SetCountry(string countryCode)
    {
        var code = Text.Clean(countryCode).ToUpperInvariant();
        if (code.Length != 2)
            return DomainError.Validation("company.country", "Elige el país de la lista.", "countryCode");

        CountryCode = code;
        return Result.Success();
    }

    public void MarkProvisioned() => IsProvisioned = true;
}
