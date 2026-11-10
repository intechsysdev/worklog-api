using WorkLog.Domain.Common;

namespace WorkLog.Domain.Access;

/// <summary>
/// Sesión de la app de una persona con contraseña de WorkLog. Los tokens se entregan una vez y aquí
/// solo queda su resumen (SHA-256): con la base no se puede suplantar a nadie. Cada renovación
/// cambia ambos tokens; el refresh anterior se recuerda para detectar si alguien lo reutiliza.
/// </summary>
public sealed class MobileSession : TenantEntity
{
    private MobileSession()
    {
    }

    private MobileSession(Guid tenantId) : base(tenantId)
    {
    }

    public Guid MemberId { get; private set; }
    public string AccessTokenHash { get; private set; } = string.Empty;
    public DateTimeOffset AccessExpiresAt { get; private set; }
    public string RefreshTokenHash { get; private set; } = string.Empty;
    public string? PreviousRefreshTokenHash { get; private set; }
    public DateTimeOffset RefreshExpiresAt { get; private set; }
    public DateTimeOffset StartedAt { get; private set; }
    public DateTimeOffset? RenewedAt { get; private set; }

    /// <summary>Modelo del teléfono, como lo reporta la app. Solo para reconocer la sesión.</summary>
    public string? Device { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }
    public string? RevokedReason { get; private set; }

    public bool IsRevoked => RevokedAt is not null;

    public static MobileSession Start(Guid tenantId, Guid memberId, SessionTokens tokens, string? device, DateTimeOffset now) => new(tenantId)
    {
        MemberId = memberId,
        AccessTokenHash = tokens.AccessHash,
        AccessExpiresAt = tokens.AccessExpiresAt,
        RefreshTokenHash = tokens.RefreshHash,
        RefreshExpiresAt = tokens.RefreshExpiresAt,
        StartedAt = now,
        Device = Text.CleanOrNull(device) is { } d ? d[..Math.Min(d.Length, 120)] : null
    };

    public bool AccessValid(DateTimeOffset now) => !IsRevoked && AccessExpiresAt > now;

    public bool RefreshValid(DateTimeOffset now) => !IsRevoked && RefreshExpiresAt > now;

    public void Renew(SessionTokens tokens, DateTimeOffset now)
    {
        PreviousRefreshTokenHash = RefreshTokenHash;
        AccessTokenHash = tokens.AccessHash;
        AccessExpiresAt = tokens.AccessExpiresAt;
        RefreshTokenHash = tokens.RefreshHash;
        RefreshExpiresAt = tokens.RefreshExpiresAt;
        RenewedAt = now;
    }

    public void Revoke(DateTimeOffset now, string reason)
    {
        if (IsRevoked) return;
        RevokedAt = now;
        RevokedReason = reason.Length > 60 ? reason[..60] : reason;
    }
}

/// <summary>Resúmenes y vencimientos de un par de tokens recién emitidos.</summary>
public sealed record SessionTokens(string AccessHash, DateTimeOffset AccessExpiresAt, string RefreshHash, DateTimeOffset RefreshExpiresAt);

/// <summary>Reglas de la contraseña de la app: se escribe en un teléfono, así que pide largo antes que símbolos.</summary>
public static class PasswordPolicy
{
    public const int MinLength = 8;
    public const int MaxLength = 128;

    public static DomainError? Validate(string? password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinLength)
            return DomainError.Validation("password.short", $"La contraseña debe tener al menos {MinLength} caracteres.", "newPassword");
        if (password.Length > MaxLength)
            return DomainError.Validation("password.long", $"La contraseña puede tener hasta {MaxLength} caracteres.", "newPassword");
        if (!password.Any(char.IsLetter) || !password.Any(char.IsDigit))
            return DomainError.Validation("password.weak", "La contraseña debe tener letras y números.", "newPassword");
        return null;
    }
}
