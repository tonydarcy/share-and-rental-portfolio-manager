using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Accounts;
using PortfolioManager.Core.Security;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Account;

/// <summary>Turn two-factor sign-in (an authenticator app code after the password) on or off, and make new recovery codes.</summary>
[AllowViewOnly]
public class TwoFactorModel(AccountService accounts, AccountTokens tokens, SiteBranding site, TimeProvider clock,
    AccountSecurity security) : PageModel
{
    /// <summary>The new secret being set up, carried in the form until the first code checks out.</summary>
    [BindProperty]
    public string? Secret { get; set; }

    [BindProperty]
    public string? Code { get; set; }

    public bool IsOn { get; private set; }
    public int RecoveryCodesLeft { get; private set; }
    public string? AuthenticatorUri { get; private set; }

    /// <summary>Shown once, straight after they are made.</summary>
    public IReadOnlyList<string> NewRecoveryCodes { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync()
    {
        if (await LoadAsync() is null)
            return RedirectToPage("/Account/Login");
        if (!IsOn)
            StartSetup();
        return Page();
    }

    public async Task<IActionResult> OnPostEnableAsync()
    {
        if (await LoadAsync() is not { } user)
            return RedirectToPage("/Account/Login");
        if (IsOn)
            return RedirectToPage();
        if (string.IsNullOrEmpty(Secret) || !TwoFactor.VerifyCode(Secret, Code, clock.GetUtcNow()))
        {
            ModelState.AddModelError(string.Empty, "That code isn't right. Check the time on your phone is correct, and enter the newest code.");
            StartSetup(Secret);
            return Page();
        }

        var (codes, stored) = TwoFactor.NewRecoveryCodes();
        await accounts.SetTwoFactorAsync(user.UserId, tokens.ProtectSecret(Secret), stored);
        await security.AddAsync(HttpContext, user.UserId, user.Email, SecurityEvents.TwoFactorOn);
        await SignInFlow.RefreshAsync(HttpContext, user with { TwoFactorSecret = Secret });
        IsOn = true;
        RecoveryCodesLeft = codes.Count;
        NewRecoveryCodes = codes;
        return Page();
    }

    public async Task<IActionResult> OnPostRecoveryCodesAsync()
    {
        if (await LoadAsync() is not { HasTwoFactor: true } user)
            return RedirectToPage();
        if (!CodeIsRight(user))
        {
            ModelState.AddModelError(string.Empty, "That code isn't right. Enter the newest code from your authenticator app.");
            return Page();
        }
        var (codes, stored) = TwoFactor.NewRecoveryCodes();
        await accounts.SetTwoFactorAsync(user.UserId, user.TwoFactorSecret, stored);
        await security.AddAsync(HttpContext, user.UserId, user.Email, SecurityEvents.RecoveryCodes);
        RecoveryCodesLeft = codes.Count;
        NewRecoveryCodes = codes;
        return Page();
    }

    public async Task<IActionResult> OnPostDisableAsync()
    {
        if (await LoadAsync() is not { HasTwoFactor: true } user)
            return RedirectToPage();
        if (!CodeIsRight(user))
        {
            ModelState.AddModelError(string.Empty, "That code isn't right. Enter the newest code from your authenticator app, or a recovery code.");
            return Page();
        }
        await accounts.SetTwoFactorAsync(user.UserId, null, null);
        await security.AddAsync(HttpContext, user.UserId, user.Email, SecurityEvents.TwoFactorOff);
        await SignInFlow.RefreshAsync(HttpContext, user with { TwoFactorSecret = null });
        TempData["Message"] = "Two-factor sign-in is off. You'll log in with just your password.";
        return RedirectToPage();
    }

    private bool CodeIsRight(SignedInUser user) =>
        TwoFactor.VerifyCode(tokens.UnprotectSecret(user.TwoFactorSecret), Code, clock.GetUtcNow())
        || TwoFactor.UseRecoveryCode(user.RecoveryCodes, Code) is not null;

    private async Task<SignedInUser?> LoadAsync()
    {
        var user = await accounts.FindByIdAsync(CurrentPortfolio.UserId(User));
        IsOn = user?.HasTwoFactor == true;
        RecoveryCodesLeft = TwoFactor.RecoveryCodesLeft(user?.RecoveryCodes);
        return user;
    }

    private void StartSetup(string? secret = null)
    {
        Secret = string.IsNullOrEmpty(secret) ? TwoFactor.NewSecret() : secret;
        Code = null;
        ModelState.Remove(nameof(Secret));
        ModelState.Remove(nameof(Code));
        AuthenticatorUri = TwoFactor.AuthenticatorUri(site.Name, User.Identity?.Name ?? "", Secret);
    }
}
