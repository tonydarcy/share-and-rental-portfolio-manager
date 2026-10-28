using System.Data;
using Dapper;
using PortfolioManager.Core.Data;
using PortfolioManager.Core.Security;

namespace PortfolioManager.Core.Accounts;

/// <summary>
/// A login. TwoFactorSecret is stored protected (the web site unprotects it); RecoveryCodes are hashes.
/// PasswordStamp changes whenever the password does, so a password reset link only works once.
/// </summary>
public sealed record SignedInUser(int UserId, string Email, bool EmailConfirmed = true, string? TwoFactorSecret = null,
    string? RecoveryCodes = null, string PasswordStamp = "")
{
    public bool HasTwoFactor => !string.IsNullOrEmpty(TwoFactorSecret);
}

/// <summary>Sign in against tblUsers using the same procs as the legacy Login page.</summary>
public sealed class AccountService(IDbConnectionFactory db)
{
    /// <summary>Returns the user when the email and password match, otherwise null. Every attempt is logged.</summary>
    public async Task<SignedInUser?> SignInAsync(string email, string password)
    {
        email = email.Trim();
        await using var con = db.Create();

        var details = await con.QuerySingleOrDefaultAsync<LoginDetails>(
            "uspGetLoginDetails", new { strLoginEmail = email }, commandType: CommandType.StoredProcedure);

        var needsUpgrade = false;
        var valid = details is { iUserId: > 0 }
            && PasswordHasher.VerifyPassword(password, details.strLoginPassword, out needsUpgrade);

        await con.ExecuteAsync("uspRecordLoginAttempt",
            new { iUserId = valid ? details!.iUserId : (int?)null, bSuccessful = valid },
            commandType: CommandType.StoredProcedure);

        if (!valid)
            return null;

        // Plain text passwords (and old iteration counts) are re-hashed on a successful login,
        // for every portfolio row that shares this email, as the legacy site does.
        if (needsUpgrade)
        {
            var upgraded = PasswordHasher.HashPassword(password);
            await con.ExecuteAsync("uspUpdateLoginPassword",
                new { strLoginEmail = email, strOldPassword = details!.strLoginPassword, strNewPassword = upgraded },
                commandType: CommandType.StoredProcedure);
            // The password stamp follows the stored hash, so the sign-in cookie must carry the new one
            details.strLoginPassword = upgraded;
        }

        return details!.ToUser() with { Email = email };
    }

    /// <summary>The login that uses an email, whatever its password, or null (for "forgot password" and email checks).</summary>
    public async Task<SignedInUser?> FindByEmailAsync(string email)
    {
        await using var con = db.Create();
        var details = await con.QuerySingleOrDefaultAsync<LoginDetails>(
            "uspGetLoginDetails", new { strLoginEmail = email.Trim() }, commandType: CommandType.StoredProcedure);
        return details is { iUserId: > 0 } ? details.ToUser() : null;
    }

    public async Task<SignedInUser?> FindByIdAsync(int userId)
    {
        await using var con = db.Create();
        var details = await con.QuerySingleOrDefaultAsync<LoginDetails>(
            "uspGetLoginById", new { iUserId = userId }, commandType: CommandType.StoredProcedure);
        return details is { iUserId: > 0 } ? details.ToUser() : null;
    }

    /// <summary>Sets a new password after a reset (stored as a hash). It also marks the email as checked.</summary>
    public async Task SetPasswordAsync(int userId, string newPassword)
    {
        await using var con = db.Create();
        await con.ExecuteAsync("uspSetLoginPassword", new { iUserId = userId, strNewPassword = PasswordHasher.HashPassword(newPassword) },
            commandType: CommandType.StoredProcedure);
    }

    public async Task ConfirmEmailAsync(int userId)
    {
        await using var con = db.Create();
        await con.ExecuteAsync("uspConfirmLoginEmail", new { iUserId = userId }, commandType: CommandType.StoredProcedure);
    }

    /// <summary>Turns two-factor sign-in on (protected secret and hashed recovery codes) or off (both null).</summary>
    public async Task SetTwoFactorAsync(int userId, string? protectedSecret, string? recoveryCodes)
    {
        await using var con = db.Create();
        await con.ExecuteAsync("uspSetTwoFactor",
            new { iUserId = userId, strTwoFactorSecret = protectedSecret, strTwoFactorRecoveryCodes = recoveryCodes },
            commandType: CommandType.StoredProcedure);
    }

    /// <summary>A short fingerprint of the stored password, which changes when the password does.</summary>
    public static string StampOf(string? storedPassword) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(storedPassword ?? "")))[..16];

    public const int MinPasswordLength = 10;
    public const int MaxPasswordLength = 256;

    /// <summary>Checks a new account's details before anything touches the database.</summary>
    public static IReadOnlyList<string> ValidateRegistration(string? email, string? password, string? confirmPassword)
    {
        var errors = new List<string>();
        email = email?.Trim();
        if (string.IsNullOrEmpty(email) || email.Length > 254 || email.Count(c => c == '@') != 1
            || email.StartsWith('@') || !email[(email.IndexOf('@') + 1)..].Contains('.') || email.Any(char.IsWhiteSpace))
            errors.Add("Enter a valid email address.");
        errors.AddRange(ValidatePassword(password, confirmPassword));
        return errors;
    }

    /// <summary>Checks a new password (on sign-up or after a reset).</summary>
    public static IReadOnlyList<string> ValidatePassword(string? password, string? confirmPassword)
    {
        var errors = new List<string>();
        if (string.IsNullOrEmpty(password) || password.Length < MinPasswordLength)
            errors.Add($"The password must be at least {MinPasswordLength} characters.");
        else if (password.Length > MaxPasswordLength)
            errors.Add($"The password can be at most {MaxPasswordLength} characters.");
        if (password != confirmPassword)
            errors.Add("The passwords don't match.");
        return errors;
    }

    /// <summary>
    /// Creates an account with AddNewUser, storing only the PBKDF2 hash of the password.
    /// Returns false (and creates nothing) if the email is already used.
    /// </summary>
    public async Task<bool> RegisterAsync(string email, string password, string portfolioName, string? contactName)
    {
        email = email.Trim();
        await using var con = db.Create();

        // Returns -1 when no account uses the email
        var existing = await con.ExecuteScalarAsync<int>("GetUserIDFromEmail",
            new { strLoginEmail = email }, commandType: CommandType.StoredProcedure);
        if (existing != -1)
            return false;

        await con.ExecuteAsync("AddNewUser", new
        {
            strEntityName = portfolioName.Trim(),
            strEntityType = "Individual",
            strEmail = email,
            strLoginPassword = PasswordHasher.HashPassword(password),
            strContactName = contactName?.Trim() ?? "",
        }, commandType: CommandType.StoredProcedure);
        return true;
    }

    private sealed class LoginDetails
    {
        public int iUserId { get; set; }
        public string? strLoginEmail { get; set; }
        public string? strLoginPassword { get; set; }
        public DateTime? dtmDateAccountConfirmed { get; set; }
        public string? strTwoFactorSecret { get; set; }
        public string? strTwoFactorRecoveryCodes { get; set; }

        public SignedInUser ToUser() => new(iUserId, strLoginEmail ?? "", dtmDateAccountConfirmed is not null,
            string.IsNullOrEmpty(strTwoFactorSecret) ? null : strTwoFactorSecret, strTwoFactorRecoveryCodes, StampOf(strLoginPassword));
    }
}
