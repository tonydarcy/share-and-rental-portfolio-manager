using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Payouts;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Payouts;

public class EditRentalModel(CurrentPortfolio current, PayoutService payouts) : PageModel
{
    [BindProperty]
    public RentalPayoutInput Input { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var portfolio = await current.GetSelectedAsync();
        if (portfolio is null)
            return NotFound();

        // Only a payout in the selected portfolio
        var stored = await payouts.GetRentalAsync(portfolio.PortfolioId, id);
        if (stored is null)
            return NotFound();

        Input = stored;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int id)
    {
        var portfolio = await current.GetSelectedAsync();
        if (portfolio is null)
            return NotFound();

        foreach (var error in Input.Validate())
            ModelState.AddModelError(string.Empty, error);
        if (!ModelState.IsValid)
            return Page();

        // UpdateRentalAsync checks the payout belongs to the portfolio inside its transaction
        if (!await payouts.UpdateRentalAsync(portfolio.PortfolioId, id, Input))
            return NotFound();

        TempData["Message"] = $"Updated the {Input.PropertyCode.Trim().ToUpperInvariant()} rental payout.";
        return RedirectToPage("/Payouts/Index");
    }
}
