using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Payouts;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Payouts;

public class IndexModel(CurrentPortfolio current, PayoutService payouts) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public DateTime? From { get; set; }

    [BindProperty(SupportsGet = true)]
    public DateTime? To { get; set; }

    public PortfolioSummary? Portfolio { get; private set; }
    public PayoutsSummary Summary { get; private set; } = new([], new(0, 0, 0, 0, 0, 0, 0, 0, 0));
    public (DateTime Start, DateTime End) ThisYear { get; } = PayoutService.FinancialYear(DateTime.Today);
    public (DateTime Start, DateTime End) LastYear { get; } = PayoutService.FinancialYear(DateTime.Today.AddYears(-1));

    public async Task OnGetAsync()
    {
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is null)
            return;

        if (From > To)
            (From, To) = (To, From);
        Summary = await payouts.GetPayoutsAsync(Portfolio.PortfolioId, Clamp(From), Clamp(To));
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var portfolio = await current.GetSelectedAsync();
        if (portfolio is null)
            return Forbid();

        // DeleteAsync only acts on a payout that belongs to the selected portfolio
        TempData["Message"] = await payouts.DeleteAsync(portfolio.PortfolioId, id) switch
        {
            null => "That payout wasn't found in this portfolio, so nothing was deleted.",
            { Ok: true } => "Payout deleted, with any units it reinvested (DRP).",
            { Error: var error } => $"The payout wasn't deleted: {error}",
        };
        return RedirectToPage(new { From, To });
    }

    private static DateTime? Clamp(DateTime? d) =>
        d is null ? null : d < PayoutService.EarliestDate ? PayoutService.EarliestDate : d > PayoutService.LatestDate ? PayoutService.LatestDate : d;
}
