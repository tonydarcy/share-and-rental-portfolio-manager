using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Payouts;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Payouts;

public class AddRentalModel(CurrentPortfolio current, PayoutService payouts) : PageModel
{
    [BindProperty]
    public RentalPayoutInput Input { get; set; } = new();

    public PortfolioSummary? Portfolio { get; private set; }

    public async Task OnGetAsync()
    {
        Portfolio = await current.GetSelectedAsync();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is null)
            return Forbid();

        foreach (var error in Input.Validate())
            ModelState.AddModelError(string.Empty, error);
        if (!ModelState.IsValid)
            return Page();

        await payouts.AddRentalAsync(Portfolio.PortfolioId, Input);
        TempData["Message"] = $"Added the {Input.PropertyCode.Trim().ToUpperInvariant()} rental payout of {Input.Net:C}.";
        return RedirectToPage("/Payouts/Index");
    }
}
