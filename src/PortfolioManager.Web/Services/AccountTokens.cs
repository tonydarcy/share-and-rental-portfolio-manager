using Microsoft.AspNetCore.DataProtection;
using PortfolioManager.Core.Accounts;

namespace PortfolioManager.Web.Services;

/// <summary>
/// Signed, time-limited tokens for links in account emails, and protection for two-factor secrets. They use the same
/// Data Protection keys as the sign-in cookie (DataProtection:KeysPath), so no token table is needed. A password reset
/// token carries a fingerprint of the current password, so it stops working once the password changes.
/// </summary>
public sealed class AccountTokens(IDataProtectionProvider provider)
{
    public const string PasswordResetPurpose = "reset";
    public const string ConfirmEmailPurpose = "confirm";
    public const string TwoFactorPurpose = "2fa";

    public static readonly TimeSpan PasswordResetLife = TimeSpan.FromHours(2);
    public static readonly TimeSpan ConfirmEmailLife = TimeSpan.FromDays(3);
    public static readonly TimeSpan TwoFactorLife = TimeSpan.FromMinutes(10);

    private readonly ITimeLimitedDataProtector tokens = provider.CreateProtector("PortfolioManager.AccountTokens").ToTimeLimitedDataProtector();
    private readonly IDataProtector secrets = provider.CreateProtector("PortfolioManager.TwoFactorSecret");

    public string PasswordReset(SignedInUser user) => Make(PasswordResetPurpose, user.UserId, user.PasswordStamp, PasswordResetLife);

    public string ConfirmEmail(SignedInUser user) => Make(ConfirmEmailPurpose, user.UserId, user.Email.ToLowerInvariant(), ConfirmEmailLife);

    /// <summary>Carries a login between the password and the authenticator code (in a short-lived cookie).</summary>
    public string TwoFactorPending(SignedInUser user, bool rememberMe) =>
        Make(TwoFactorPurpose, user.UserId, $"{user.PasswordStamp}:{(rememberMe ? 1 : 0)}", TwoFactorLife);

    public string Make(string purpose, int userId, string check, TimeSpan life) =>
        tokens.Protect($"{purpose}|{userId}|{check}", life);

    /// <summary>The login id and check text in a token, or null when it is forged, expired or for another purpose.</summary>
    public (int UserId, string Check)? Read(string purpose, string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;
        try
        {
            var parts = tokens.Unprotect(token.Trim()).Split('|', 3);
            return parts.Length == 3 && parts[0] == purpose && int.TryParse(parts[1], out var userId) ? (userId, parts[2]) : null;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    public string ProtectSecret(string secret) => secrets.Protect(secret);

    /// <summary>Null when the secret can't be read (for example the Data Protection keys were lost).</summary>
    public string? UnprotectSecret(string? stored)
    {
        if (string.IsNullOrEmpty(stored))
            return null;
        try
        {
            return secrets.Unprotect(stored);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }
}
