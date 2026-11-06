namespace WorkLog.Domain.Common;

/// <summary>Validaciones repetidas en los constructores de las entidades.</summary>
public static class Guard
{
    public static DomainError? Name(string cleaned, string field, int min, int max, string label)
    {
        if (cleaned.Length < min)
            return DomainError.Validation($"{field}.too_short", $"{label} debe tener al menos {min} caracteres.", field);

        if (cleaned.Length > max)
            return DomainError.Validation($"{field}.too_long", $"{label} admite máximo {max} caracteres.", field);

        return null;
    }

    public static DomainError? MaxLength(string? cleaned, string field, int max, string label) =>
        cleaned is not null && cleaned.Length > max
            ? DomainError.Validation($"{field}.too_long", $"{label} admite máximo {max} caracteres.", field)
            : null;

    public static DomainError? Range(int value, int min, int max, string field, string label) =>
        value < min || value > max
            ? DomainError.Validation($"{field}.out_of_range", $"{label} debe estar entre {min} y {max}.", field)
            : null;
}
