using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.CorporateActions;
using PortfolioManager.Core.Payouts;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Payouts;

public class AddModel(CurrentPortfolio current, PayoutService payouts, AnnouncedActionsService announcedActions, TimeProvider clock) : PageModel
{
    [BindProperty]
    public PayoutInput Input { get; set; } = new();

    public PortfolioSummary? Portfolio { get; private set; }

    /// <param name="announced">An announced dividend to start from (Corporate actions > Announced).</param>
    public async Task OnGetAsync(int? announced)
    {
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is not null && announced is { } id
            && await announcedActions.GetDividendAsync(Portfolio.PortfolioId, id, DateOnly.FromDateTime(clock.GetLocalNow().DateTime)) is { } d)
            Input = AnnouncedPayouts.ToPayout(d);
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

        await payouts.AddAsync(Portfolio.PortfolioId, Input);
        TempData["Message"] = $"Added the {Input.StockCode.Trim().ToUpperInvariant()} payout of {Input.Total:C}.";
        return RedirectToPage("/Payouts/Index");
    }
}
