using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Accounts;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Account;

/// <summary>
/// Permanently deletes the signed-in account, after its password and email are typed. Portfolios nobody else uses
/// go with it; portfolios shared with others stay with them (uspDeleteLogin).
/// </summary>
[AllowViewOnly]
public class DeleteModel(AccountService accounts, DeletionService deletion) : PageModel
{
    [BindProperty]
    public string? Password { get; set; }

    [BindProperty]
    public string? ConfirmEmail { get; set; }

    public string Email { get; private set; } = "";
    public IReadOnlyList<AccountPortfolioPlan> Plan { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync()
    {
        if (await LoadAsync() is null)
            return RedirectToPage("/Account/Login");
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (await LoadAsync() is not { } user)
            return RedirectToPage("/Account/Login");

        if (!DeletionService.Confirms(ConfirmEmail, Email))
            ModelState.AddModelError(nameof(ConfirmEmail), $"Type your email, {Email}, to confirm.");
        else if (string.IsNullOrEmpty(Password) || await accounts.SignInAsync(Email, Password) is not { } check || check.UserId != user.UserId)
            ModelState.AddModelError(nameof(Password), "That password isn't right.");
        if (!ModelState.IsValid)
            return Page();

        var (result, deleted, kept) = await deletion.DeleteAccountAsync(user.UserId);
        if (!result.Ok)
        {
            ModelState.AddModelError(string.Empty, result.Error!);
            return Page();
        }

        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        Response.Cookies.Delete(CurrentPortfolio.CookieName);
        TempData["Message"] = "Your account has been deleted"
            + (deleted > 0 ? $", with {deleted} portfolio{(deleted == 1 ? "" : "s")} only you used" : "")
            + (kept > 0 ? $". {kept} portfolio{(kept == 1 ? " was" : "s were")} kept for the people you shared {(kept == 1 ? "it" : "them")} with" : "")
            + ".";
        return RedirectToPage("/Account/Login");
    }

    private async Task<SignedInUser?> LoadAsync()
    {
        var user = await accounts.FindByIdAsync(CurrentPortfolio.UserId(User));
        if (user is null)
            return null;
        Email = user.Email;
        Plan = await deletion.GetAccountPlanAsync(user.UserId);
        return user;
    }
}
