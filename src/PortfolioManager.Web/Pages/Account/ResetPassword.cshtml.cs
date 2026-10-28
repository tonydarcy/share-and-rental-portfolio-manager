using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using PortfolioManager.Core.Accounts;
using PortfolioManager.Core.Security;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Account;

/// <summary>Choose a new password from an emailed link. The link stops working once the password changes, or after 2 hours.</summary>
[AllowAnonymous]
[AllowViewOnly]
[EnableRateLimiting(SignInFlow.RateLimit)]
public class ResetPasswordModel(AccountService accounts, AccountTokens tokens, AccountSecurity security) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? Token { get; set; }

    [BindProperty, DataType(DataType.Password), Display(Name = "New password")]
    public string? Password { get; set; }

    [BindProperty, DataType(DataType.Password), Display(Name = "Confirm new password")]
    public string? ConfirmPassword { get; set; }

    public bool LinkWorks { get; private set; }

    public async Task OnGetAsync() => LinkWorks = await FindUserAsync() is not null;

    public async Task<IActionResult> OnPostAsync()
    {
        var user = await FindUserAsync();
        LinkWorks = user is not null;
        if (user is null)
            return Page();

        foreach (var error in AccountService.ValidatePassword(Password, ConfirmPassword))
            ModelState.AddModelError(string.Empty, error);
        if (!ModelState.IsValid)
            return Page();

        await accounts.SetPasswordAsync(user.UserId, Password!);
        // Also lifts a pause from wrong attempts; every browser signed in with the old password is signed out within a minute
        await security.AddAsync(HttpContext, user.UserId, user.Email, SecurityEvents.PasswordReset);
        TempData["Message"] = "Your password has been changed, and any other browsers signed in to your account have been signed out. Log in with the new one.";
        return RedirectToPage("/Account/Login");
    }

    private async Task<SignedInUser?> FindUserAsync()
    {
        if (tokens.Read(AccountTokens.PasswordResetPurpose, Token) is not { } token)
            return null;
        var user = await accounts.FindByIdAsync(token.UserId);
        return user is not null && user.PasswordStamp == token.Check ? user : null;
    }
}
