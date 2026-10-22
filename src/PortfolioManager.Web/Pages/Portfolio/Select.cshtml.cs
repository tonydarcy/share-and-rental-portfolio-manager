using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Portfolio;

/// <summary>Handles the portfolio picker in the navbar.</summary>
[AllowViewOnly]
public class SelectModel(CurrentPortfolio current) : PageModel
{
    public IActionResult OnGet() => RedirectToPage("/Index");

    public async Task<IActionResult> OnPostAsync(int portfolioId, string? returnUrl)
    {
        if (!await current.TrySelectAsync(portfolioId))
            return Forbid();

        return Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl) : RedirectToPage("/Index");
    }
}
