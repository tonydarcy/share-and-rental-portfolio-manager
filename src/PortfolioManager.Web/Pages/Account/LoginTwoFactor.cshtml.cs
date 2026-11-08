using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using PortfolioManager.Core.Accounts;
using PortfolioManager.Core.Security;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Account;

/// <summary>The second step of logging in when two-factor sign-in is on: the authenticator app's code, or a recovery code.</summary>
[AllowAnonymous]
[AllowViewOnly]
[EnableRateLimiting(SignInFlow.RateLimit)]
public class LoginTwoFactorModel(AccountService accounts, AccountTokens tokens, SignInFlow signIn, TimeProvider clock,
    AccountSecurity security) : PageModel
{
    // Named "totp" and marked one-time-code in the page, so password managers (iCloud Keychain, Bitwarden,
    // Proton Pass, 1Password) see it as the place for the authenticator code and offer to fill it.
    [BindProperty(Name = "totp"), Display(Name = "6-digit code")]
    public string? Totp { get; set; }

    /// <summary>A one-off recovery code, typed in its own box because it has letters (the code box shows a number pad).</summary>
    [BindProperty, Display(Name = "Recovery code")]
    public string? RecoveryCode { get; set; }

    /// <summary>The login's email, shown read-only so password managers know which saved login's code to fill.</summary>
    public string Email { get; private set; } = "";

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (await PendingAsync() is not { } pending)
            return RedirectToPage("/Account/Login", new { ReturnUrl });
        Email = pending.User.Email;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (await PendingAsync() is not { } pending)
        {
            TempData["Error"] = "That took too long. Please log in again.";
            return RedirectToPage("/Account/Login", new { ReturnUrl });
        }
        var (user, rememberMe) = pending;
        Email = user.Email;
        var code = EnteredCode(Totp, RecoveryCode);
        if (code is null)
            ModelState.AddModelError(string.Empty, "Enter the 6-digit code from your authenticator app, or a recovery code.");
        if (!ModelState.IsValid)
            return Page();

        if (await security.IsPausedAsync(user.Email))
        {
            ModelState.AddModelError(string.Empty, AccountSecurity.PausedMessage);
            return Page();
        }
        var secret = tokens.UnprotectSecret(user.TwoFactorSecret);
        if (!TwoFactor.VerifyCode(secret, code, clock.GetUtcNow()))
        {
            // Not an authenticator code: maybe one of the one-off recovery codes
            if (TwoFactor.UseRecoveryCode(user.RecoveryCodes, code) is not { } remaining)
            {
                var paused = await security.FailedAsync(HttpContext, SecurityEvents.TwoFactorCode, user.Email, user);
                ModelState.AddModelError(string.Empty, paused ? AccountSecurity.PausedMessage
                    : "That code isn't right. Check the time on your phone is correct, and use the newest code.");
                return Page();
            }
            await accounts.SetTwoFactorAsync(user.UserId, user.TwoFactorSecret, remaining);
            TempData["Message"] = $"You used a recovery code; {TwoFactor.RecoveryCodesLeft(remaining)} are left. Make new ones under Two-factor sign-in if you're running low.";
        }

        if (await signIn.SignInAsync(HttpContext, user, rememberMe) is { } message)
            TempData["Message"] = message;
        return Url.IsLocalUrl(ReturnUrl) ? LocalRedirect(ReturnUrl) : RedirectToPage("/Index");
    }

    /// <summary>The code typed: the recovery code box if it was used, otherwise the authenticator code box.</summary>
    public static string? EnteredCode(string? totp, string? recoveryCode) =>
        !string.IsNullOrWhiteSpace(recoveryCode) ? recoveryCode.Trim()
        : !string.IsNullOrWhiteSpace(totp) ? totp.Trim()
        : null;

    /// <summary>The login whose password was just checked, from the short-lived cookie the login page set.</summary>
    private async Task<(SignedInUser User, bool RememberMe)?> PendingAsync()
    {
        if (tokens.Read(AccountTokens.TwoFactorPurpose, Request.Cookies[SignInFlow.TwoFactorCookie]) is not { } token)
            return null;
        var parts = token.Check.Split(':');
        var user = await accounts.FindByIdAsync(token.UserId);
        return user is { HasTwoFactor: true } && parts.Length == 2 && user.PasswordStamp == parts[0] ? (user, parts[1] == "1") : null;
    }
}
