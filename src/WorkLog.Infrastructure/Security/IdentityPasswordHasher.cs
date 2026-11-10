using Microsoft.AspNetCore.Identity;
using WorkLog.Application.Abstractions;

namespace WorkLog.Infrastructure.Security;

/// <summary>PBKDF2 con sal y costo del hasher de ASP.NET Core Identity (formato v3).</summary>
internal sealed class IdentityPasswordHasher : IPasswordHasher
{
    private static readonly object User = new();
    private readonly PasswordHasher<object> _hasher = new();

    public string Hash(string password) => _hasher.HashPassword(User, password);

    public bool Verify(string hash, string password) =>
        _hasher.VerifyHashedPassword(User, hash, password) is PasswordVerificationResult.Success or PasswordVerificationResult.SuccessRehashNeeded;
}
