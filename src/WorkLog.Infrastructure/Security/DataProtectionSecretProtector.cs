using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using WorkLog.Application.Abstractions;

namespace WorkLog.Infrastructure.Security;

/// <summary>
/// Cifra con Data Protection de ASP.NET Core. En App Service las claves se guardan solas en
/// %HOME%\ASP.NET\DataProtection-Keys, que sobrevive a reinicios y despliegues.
/// </summary>
internal sealed class DataProtectionSecretProtector(IDataProtectionProvider provider) : ISecretProtector
{
    private readonly IDataProtector _protector = provider.CreateProtector("WorkLog.OneCredential.v1");

    public string Protect(string value) => _protector.Protect(value);

    public string? Unprotect(string value)
    {
        try
        {
            return _protector.Unprotect(value);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}
