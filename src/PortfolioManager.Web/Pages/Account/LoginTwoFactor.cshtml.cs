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
public class LoginTwoFactorModel(AccountService accounts, AccountTokens tokens, SignInFlow signIn, TimeProvider clock) : PageModel
{
    [BindProperty, Required, Display(Name = "Code from your authenticator app, or a recovery code")]
    public string Code { get; set; } = "";

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public async Task<IActionResult> OnGetAsync() =>
        await PendingAsync() is null ? RedirectToPage("/Account/Login", new { ReturnUrl }) : Page();

    public async Task<IActionResult> OnPostAsync()
    {
        if (await PendingAsync() is not { } pending)
        {
            TempData["Error"] = "That took too long. Please log in again.";
            return RedirectToPage("/Account/Login", new { ReturnUrl });
        }
        if (!ModelState.IsValid)
            return Page();

        var (user, rememberMe) = pending;
        var secret = tokens.UnprotectSecret(user.TwoFactorSecret);
        if (!TwoFactor.VerifyCode(secret, Code, clock.GetUtcNow()))
        {
            // Not an authenticator code: maybe one of the one-off recovery codes
            if (TwoFactor.UseRecoveryCode(user.RecoveryCodes, Code) is not { } remaining)
            {
                ModelState.AddModelError(string.Empty, "That code isn't right. Check the time on your phone is correct, and use the newest code.");
                return Page();
            }
            await accounts.SetTwoFactorAsync(user.UserId, user.TwoFactorSecret, remaining);
            TempData["Message"] = $"You used a recovery code; {TwoFactor.RecoveryCodesLeft(remaining)} are left. Make new ones under Two-factor sign-in if you're running low.";
        }

        if (await signIn.SignInAsync(HttpContext, user, rememberMe) is { } message)
            TempData["Message"] = message;
        return Url.IsLocalUrl(ReturnUrl) ? LocalRedirect(ReturnUrl) : RedirectToPage("/Index");
    }

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
