using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Payouts;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Core.TaxWork;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Payouts;

public class AddRentalModel(CurrentPortfolio current, PayoutService payouts, RentalService rentals) : PageModel
{
    [BindProperty]
    public RentalPayoutInput Input { get; set; } = new();

    public PortfolioSummary? Portfolio { get; private set; }

    /// <summary>The portfolio's rental properties, to fill the property code from.</summary>
    public IReadOnlyList<RentalProperty> Properties { get; private set; } = [];

    public async Task OnGetAsync(string? code)
    {
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is null)
            return;
        Properties = await rentals.GetPropertiesAsync(Portfolio.PortfolioId);
        if (!string.IsNullOrWhiteSpace(code))
            Input.PropertyCode = code.Trim().ToUpperInvariant();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is null)
            return Forbid();

        foreach (var error in Input.Validate())
            ModelState.AddModelError(string.Empty, error);
        if (!ModelState.IsValid)
        {
            Properties = await rentals.GetPropertiesAsync(Portfolio.PortfolioId);
            return Page();
        }

        await payouts.AddRentalAsync(Portfolio.PortfolioId, Input);
        TempData["Message"] = $"Added the {Input.PropertyCode.Trim().ToUpperInvariant()} rental payout of {Input.Net:C}.";
        return RedirectToPage("/Payouts/Index");
    }
}
