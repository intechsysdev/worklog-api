using WorkLog.Domain.Common;

namespace WorkLog.Domain.Privacy;

/// <summary>
/// Versión de la política de tratamiento de datos (Ley 1581 de 2012). La app exige aceptar la
/// vigente antes de registrar ubicación.
/// </summary>
public sealed class PrivacyPolicyVersion : Entity
{
    private PrivacyPolicyVersion()
    {
    }

    public string Version { get; private set; } = string.Empty;
    public DateOnly EffectiveFrom { get; private set; }

    /// <summary>Resumen en puntos, separados por salto de línea, que muestra la app.</summary>
    public string Summary { get; private set; } = string.Empty;

    public string? FullTextUrl { get; private set; }

    public IReadOnlyList<string> SummaryPoints =>
        Summary.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static PrivacyPolicyVersion Create(string version, DateOnly effectiveFrom, IEnumerable<string> points, string? fullTextUrl) => new()
    {
        Version = Text.Clean(version),
        EffectiveFrom = effectiveFrom,
        Summary = string.Join('\n', points.Select(Text.Clean)),
        FullTextUrl = Text.CleanOrNull(fullTextUrl)
    };
}
