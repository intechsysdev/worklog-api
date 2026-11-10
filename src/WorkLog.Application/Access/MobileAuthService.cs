using System.Security.Cryptography;
using System.Text;
using WorkLog.Application.Abstractions;
using WorkLog.Application.Common;
using WorkLog.Domain.Access;

namespace WorkLog.Application.Access;

public sealed record MobileLoginCommand(string? Email, string? Password, Guid? TenantId, string? Device);

public sealed record MobileTenantOption(Guid TenantId, string Name);

public sealed record MobileUserDto(Guid MemberId, string FullName, string Email);

/// <summary>Tokens de la app. <paramref name="ExpiresAt"/> es el vencimiento del de acceso (una hora).</summary>
public sealed record MobileSessionDto(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset ExpiresAt,
    bool MustChangePassword,
    MobileUserDto User,
    Guid TenantId,
    string TenantName);

/// <summary>Sesión, o la lista de empresas si el correo y la contraseña sirven en varias (se elige y se repite).</summary>
public sealed record MobileLoginResult(MobileSessionDto? Session, IReadOnlyList<MobileTenantOption>? Tenants);

public sealed record RefreshSessionCommand(string? RefreshToken);

public sealed record ChangePasswordCommand(string? CurrentPassword, string? NewPassword);

/// <summary>Quién está detrás de un token de la app, para autenticar la petición.</summary>
public sealed record AppSession(Guid SessionId, Guid TenantId, Guid MemberId, string Email, string FullName, bool MustChangePassword);

/// <summary>
/// Puerto de entrada: ingreso a la app con la contraseña de WorkLog (los usuarios móviles no tienen
/// cuenta de ONE). Los tokens son opacos y llevan el prefijo <see cref="MobileTokens.Prefix"/>.
/// </summary>
public interface IMobileAuthService
{
    Task<OperationResult<MobileLoginResult>> LoginAsync(MobileLoginCommand command, CancellationToken ct = default);

    Task<OperationResult<MobileSessionDto>> RefreshAsync(RefreshSessionCommand command, CancellationToken ct = default);

    Task<OperationResult> LogoutAsync(RefreshSessionCommand command, CancellationToken ct = default);

    /// <summary>Null si el token no existe, venció, se revocó o la persona ya no puede entrar.</summary>
    Task<AppSession?> AuthenticateAsync(string accessToken, CancellationToken ct = default);

    Task<OperationResult> ChangePasswordAsync(AppSession session, ChangePasswordCommand command, CancellationToken ct = default);
}

/// <summary>Emisión y resumen de los tokens de la app.</summary>
public static class MobileTokens
{
    public const string Prefix = "wlm_";

    public static readonly TimeSpan AccessLifetime = TimeSpan.FromHours(1);

    /// <summary>Cada renovación la extiende: un técnico que abre la app al menos una vez al mes no vuelve a escribir la contraseña.</summary>
    public static readonly TimeSpan RefreshLifetime = TimeSpan.FromDays(30);

    public static bool IsAppToken(string token) => token.StartsWith(Prefix, StringComparison.Ordinal);

    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static (string Access, string Refresh, SessionTokens Hashes) Issue(DateTimeOffset now)
    {
        var access = Prefix + Base64Url(RandomNumberGenerator.GetBytes(32));
        var refresh = Prefix + Base64Url(RandomNumberGenerator.GetBytes(32));
        return (access, refresh, new SessionTokens(Hash(access), now + AccessLifetime, Hash(refresh), now + RefreshLifetime));
    }

    /// <summary>Contraseña temporal fácil de dictar: sin 0/O ni 1/l/I, con letras y números.</summary>
    public static string TemporaryPassword()
    {
        const string letters = "abcdefghjkmnpqrstuvwxyz";
        const string digits = "23456789";
        const string all = letters + digits;
        var chars = new char[10];
        chars[0] = letters[RandomNumberGenerator.GetInt32(letters.Length)];
        chars[1] = digits[RandomNumberGenerator.GetInt32(digits.Length)];
        for (var i = 2; i < chars.Length; i++) chars[i] = all[RandomNumberGenerator.GetInt32(all.Length)];
        RandomNumberGenerator.Shuffle(chars.AsSpan());
        return new string(chars);
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

internal sealed class MobileAuthService(
    IMobileLoginDirectory directory,
    IMobileSessionRepository sessions,
    IPasswordHasher hasher,
    ITenantConfiguration configuration,
    IAuditTrail audit,
    IUnitOfWork unitOfWork,
    TimeProvider clock) : IMobileAuthService
{
    private const string WrongCredentials = "Correo o contraseña incorrectos.";
    private const string SessionEnded = "Tu sesión terminó. Vuelve a ingresar.";

    /// <summary>Hash de una contraseña cualquiera: se verifica igual cuando el correo no existe, para no delatarlo por el tiempo.</summary>
    private static string? decoyHash;

    public async Task<OperationResult<MobileLoginResult>> LoginAsync(MobileLoginCommand command, CancellationToken ct = default)
    {
        var email = Member.NormalizeEmail(command.Email);
        var password = command.Password ?? string.Empty;
        if (email.Length == 0 || password.Length == 0)
            return OperationResult<MobileLoginResult>.Invalid("Escribe tu correo y tu contraseña.", "email");

        var now = clock.GetUtcNow();
        var candidates = (await directory.FindByEmailAsync(email, ct))
            .Where(c => command.TenantId is null || c.TenantId == command.TenantId)
            .ToList();

        if (candidates.Count == 0)
        {
            hasher.Verify(decoyHash ??= hasher.Hash("worklog-decoy-9"), password);
            return Unauthorized<MobileLoginResult>(WrongCredentials, "auth.invalid");
        }

        var matches = new List<LoginCandidate>();
        DateTimeOffset? lockedUntil = null;
        foreach (var candidate in candidates)
        {
            var member = candidate.Member;
            if (member.IsLockedOut(now))
            {
                lockedUntil = member.LockedUntil;
                continue;
            }

            if (hasher.Verify(member.PasswordHash!, password)) matches.Add(candidate);
            else member.RegisterFailedLogin(now);
        }

        if (matches.Count == 0)
        {
            await unitOfWork.SaveChangesAsync(ct);
            if (lockedUntil is { } until || candidates.Any(c => c.Member.IsLockedOut(now)))
            {
                var minutes = Math.Max(1, (int)Math.Ceiling(((lockedUntil ?? candidates.Max(c => c.Member.LockedUntil)!.Value) - now).TotalMinutes));
                return OperationResult<MobileLoginResult>.Forbidden(
                    $"Demasiados intentos fallidos. Intenta de nuevo en {minutes} minuto(s) o pide a tu administrador que restablezca tu contraseña.",
                    "auth.locked");
            }

            return Unauthorized<MobileLoginResult>(WrongCredentials, "auth.invalid");
        }

        // El mismo correo y contraseña sirven en varias empresas: la app pregunta en cuál y repite el ingreso.
        if (matches.Count > 1)
            return new MobileLoginResult(null, matches.Select(m => new MobileTenantOption(m.TenantId, m.TenantName)).ToList());

        var match = matches[0];
        if (await LicenseProblemAsync(match.TenantId, match.TenantName, ct) is { } license)
            return OperationResult<MobileLoginResult>.Forbidden(license, "auth.license");

        var wasInvited = match.Member.Status == MemberStatus.Invited;
        match.Member.RegisterLogin(now);
        var (access, refresh, hashes) = MobileTokens.Issue(now);
        sessions.Add(MobileSession.Start(match.TenantId, match.Member.Id, hashes, command.Device, now));
        if (wasInvited)
        {
            audit.RecordSystem(match.TenantId, "member.app_activated", $"{match.Member.FullName} entró a la app por primera vez y quedó activo.",
                "Usuarios", match.Member.Id.ToString());
        }

        await unitOfWork.SaveChangesAsync(ct);
        return new MobileLoginResult(ToDto(access, refresh, hashes, match), null);
    }

    public async Task<OperationResult<MobileSessionDto>> RefreshAsync(RefreshSessionCommand command, CancellationToken ct = default)
    {
        var token = command.RefreshToken?.Trim() ?? string.Empty;
        if (!MobileTokens.IsAppToken(token)) return Unauthorized<MobileSessionDto>(SessionEnded, "auth.session_ended");

        var now = clock.GetUtcNow();
        var hash = MobileTokens.Hash(token);
        var session = await sessions.FindByRefreshHashAsync(hash, ct);
        if (session is null || !session.RefreshValid(now)) return Unauthorized<MobileSessionDto>(SessionEnded, "auth.session_ended");

        // Un refresh ya usado que vuelve a llegar es señal de que alguien lo copió: se cierra la sesión.
        if (session.RefreshTokenHash != hash)
        {
            session.Revoke(now, "refresh reutilizado");
            await unitOfWork.SaveChangesAsync(ct);
            return Unauthorized<MobileSessionDto>(SessionEnded, "auth.session_ended");
        }

        var candidate = await directory.FindAsync(session.TenantId, session.MemberId, ct);
        if (candidate is null || !CanUseApp(candidate.Member))
        {
            session.Revoke(now, "persona sin acceso");
            await unitOfWork.SaveChangesAsync(ct);
            return Unauthorized<MobileSessionDto>(SessionEnded, "auth.session_ended");
        }

        if (await LicenseProblemAsync(candidate.TenantId, candidate.TenantName, ct) is { } license)
            return OperationResult<MobileSessionDto>.Forbidden(license, "auth.license");

        var (access, refresh, hashes) = MobileTokens.Issue(now);
        session.Renew(hashes, now);
        candidate.Member.Touch(now);
        await unitOfWork.SaveChangesAsync(ct);
        return ToDto(access, refresh, hashes, candidate);
    }

    public async Task<OperationResult> LogoutAsync(RefreshSessionCommand command, CancellationToken ct = default)
    {
        var token = command.RefreshToken?.Trim() ?? string.Empty;
        if (!MobileTokens.IsAppToken(token)) return OperationResult.Ok();

        var session = await sessions.FindByRefreshHashAsync(MobileTokens.Hash(token), ct);
        if (session is null) return OperationResult.Ok();

        session.Revoke(clock.GetUtcNow(), "cerró sesión");
        await unitOfWork.SaveChangesAsync(ct);
        return OperationResult.Ok();
    }

    public async Task<AppSession?> AuthenticateAsync(string accessToken, CancellationToken ct = default)
    {
        if (!MobileTokens.IsAppToken(accessToken)) return null;

        var now = clock.GetUtcNow();
        var session = await sessions.FindByAccessHashAsync(MobileTokens.Hash(accessToken), ct);
        if (session is null || !session.AccessValid(now)) return null;

        var candidate = await directory.FindAsync(session.TenantId, session.MemberId, ct);
        if (candidate is null || !CanUseApp(candidate.Member)) return null;

        var member = candidate.Member;
        return new AppSession(session.Id, session.TenantId, member.Id, member.Email, member.FullName, member.MustChangePassword);
    }

    public async Task<OperationResult> ChangePasswordAsync(AppSession session, ChangePasswordCommand command, CancellationToken ct = default)
    {
        var candidate = await directory.FindAsync(session.TenantId, session.MemberId, ct);
        if (candidate is null || !CanUseApp(candidate.Member)) return Unauthorized(SessionEnded, "auth.session_ended");

        var member = candidate.Member;
        if (!hasher.Verify(member.PasswordHash!, command.CurrentPassword ?? string.Empty))
            return OperationResult.Invalid("La contraseña actual no es correcta.", "currentPassword", "password.current");

        if (PasswordPolicy.Validate(command.NewPassword) is { } weak) return OperationResult.From(weak);
        if (command.NewPassword == command.CurrentPassword)
            return OperationResult.Invalid("La nueva contraseña debe ser distinta de la actual.", "newPassword", "password.same");

        var now = clock.GetUtcNow();
        member.ChangePassword(hasher.Hash(command.NewPassword!), now);

        // Las demás sesiones abiertas con la contraseña anterior se cierran; esta sigue.
        foreach (var other in await sessions.ListOpenAsync(member.Id, ct))
        {
            if (other.Id != session.SessionId) other.Revoke(now, "cambió la contraseña");
        }

        audit.RecordSystem(session.TenantId, "member.password_changed", $"{member.FullName} cambió su contraseña de la app.",
            "Usuarios", member.Id.ToString());
        await unitOfWork.SaveChangesAsync(ct);
        return OperationResult.Ok();
    }

    private static bool CanUseApp(Member member) =>
        member.Usage == MemberUsage.Mobile && member.HasAppPassword && member.Status != MemberStatus.Inactive;

    /// <summary>Solo bloquea si ONE dice que la licencia o la credencial no están vigentes; si ONE no responde, se deja entrar.</summary>
    private async Task<string?> LicenseProblemAsync(Guid tenantId, string tenantName, CancellationToken ct)
    {
        var config = await configuration.ForTenantAsync(tenantId, ct);
        return config.LicenseInactive
            ? $"WorkLog no está activo para {tenantName} en INTECHSYS ONE. Avisa a tu administrador."
            : null;
    }

    private static MobileSessionDto ToDto(string access, string refresh, SessionTokens hashes, LoginCandidate candidate) =>
        new(access, refresh, hashes.AccessExpiresAt, candidate.Member.MustChangePassword,
            new MobileUserDto(candidate.Member.Id, candidate.Member.FullName, candidate.Member.Email), candidate.TenantId, candidate.TenantName);

    private static OperationResult<T> Unauthorized<T>(string message, string code) => OperationResult<T>.Fail(ErrorKind.Unauthorized, message, code);

    private static OperationResult Unauthorized(string message, string code) => OperationResult.Fail(ErrorKind.Unauthorized, message, code);
}
