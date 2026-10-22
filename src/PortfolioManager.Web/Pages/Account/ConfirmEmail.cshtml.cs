using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Accounts;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Account;

/// <summary>The link in the "check your email address" email.</summary>
[AllowAnonymous]
[AllowViewOnly]
public class ConfirmEmailModel(AccountService accounts, AccountTokens tokens) : PageModel
{
    public async Task<IActionResult> OnGetAsync(string? token)
    {
        if (tokens.Read(AccountTokens.ConfirmEmailPurpose, token) is { } t
            && await accounts.FindByIdAsync(t.UserId) is { } user
            && string.Equals(user.Email, t.Check, StringComparison.OrdinalIgnoreCase))
        {
            await accounts.ConfirmEmailAsync(user.UserId);
            TempData["Message"] = "Thanks, your email address is confirmed. You can log in now.";
            return RedirectToPage("/Account/Login");
        }
        TempData["Error"] = "That link has expired or isn't complete. Send yourself a new one below.";
        return RedirectToPage("/Account/ResendConfirmation");
    }
}
