using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using PortfolioManager.Core.Exports;

namespace PortfolioManager.Web.Services;

/// <summary>
/// Encrypts Xero and MYOB sign-in tokens with ASP.NET Data Protection, like the Navexa keys, and signs the "state"
/// that goes out with a sign-in so the answer can only come back to the portfolio and login that started it.
/// </summary>
public sealed class AccountingTokenProtector(IDataProtectionProvider provider) : IAccountingTokenProtector
{
    private readonly IDataProtector _tokens = provider.CreateProtector("PortfolioManager.AccountingLink.Tokens.v1");
    private readonly ITimeLimitedDataProtector _state = provider.CreateProtector("PortfolioManager.AccountingLink.State.v1").ToTimeLimitedDataProtector();

    public string Protect(string tokens) => _tokens.Protect(tokens);

    public string? Unprotect(string protectedTokens)
    {
        try
        {
            return _tokens.Unprotect(protectedTokens);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    public string State(int portfolioId, int loginUserId, string system) =>
        _state.Protect($"{portfolioId}|{loginUserId}|{system}", TimeSpan.FromMinutes(20));

    /// <summary>The portfolio, login and system the sign-in was started for, or null when the state is forged or too old.</summary>
    public (int PortfolioId, int LoginUserId, string System)? ReadState(string? state)
    {
        try
        {
            var parts = _state.Unprotect(state ?? "").Split('|');
            return parts.Length == 3 && int.TryParse(parts[0], out var p) && int.TryParse(parts[1], out var u) && ExportSystems.All.Contains(parts[2])
                ? (p, u, parts[2]) : null;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}
