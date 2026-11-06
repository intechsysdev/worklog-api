using System.Globalization;
using System.Text;

namespace WorkLog.Domain.Common;

/// <summary>Normalización de textos que escriben los usuarios.</summary>
public static class Text
{
    /// <summary>Recorta y colapsa espacios internos. Devuelve cadena vacía para null.</summary>
    public static string Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var builder = new StringBuilder(value.Length);
        var previousSpace = false;

        foreach (var c in value.Trim())
        {
            var isSpace = char.IsWhiteSpace(c);
            if (isSpace && previousSpace) continue;
            builder.Append(isSpace ? ' ' : c);
            previousSpace = isSpace;
        }

        return builder.ToString();
    }

    /// <summary>Texto opcional: null si queda vacío tras limpiarlo.</summary>
    public static string? CleanOrNull(string? value)
    {
        var cleaned = Clean(value);
        return cleaned.Length == 0 ? null : cleaned;
    }

    /// <summary>Clave de comparación: sin acentos, sin mayúsculas y sin espacios repetidos.</summary>
    public static string Key(string? value)
    {
        var normalized = Clean(value).Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);

        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                builder.Append(char.ToLowerInvariant(c));
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>Iniciales para el avatar: primera letra del nombre y del primer apellido.</summary>
    public static string Initials(string? fullName)
    {
        var parts = Clean(fullName).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => "?",
            1 => parts[0][..1].ToUpperInvariant(),
            _ => string.Concat(parts[0][..1], parts[1][..1]).ToUpperInvariant()
        };
    }
}
