using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using PortfolioManager.Core.NavexaSync;

namespace PortfolioManager.Web.Services;

/// <summary>
/// Encrypts Navexa API keys with ASP.NET Data Protection, the same key ring that protects the sign-in cookie
/// (DataProtection:KeysPath, or the app pool's user profile). The database only ever holds the encrypted text.
/// </summary>
public sealed class NavexaKeyProtector(IDataProtectionProvider provider) : INavexaKeyProtector
{
    private readonly IDataProtector _protector = provider.CreateProtector("PortfolioManager.NavexaSync.ApiKey.v1");

    public string Protect(string apiKey) => _protector.Protect(apiKey);

    public string? Unprotect(string protectedKey)
    {
        try
        {
            return _protector.Unprotect(protectedKey);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}
