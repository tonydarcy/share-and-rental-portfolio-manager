using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Core.Tax;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Accounting;

public class JournalsModel(CurrentPortfolio current, JournalService journals, ChartOfAccountsService chart) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public DateOnly? From { get; set; }

    [BindProperty(SupportsGet = true)]
    public DateOnly? To { get; set; }

    /// <summary>Show only manual journals.</summary>
    [BindProperty(SupportsGet = true)]
    public bool Manual { get; set; }

    public PortfolioSummary? Portfolio { get; private set; }
    public string Mode { get; private set; } = "";
    public IReadOnlyList<JournalSummary> Journals { get; private set; } = [];

    public static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);
    public int ThisYear { get; } = FinancialYear.EndYearFor(Today);

    public async Task OnGetAsync()
    {
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is null)
            return;

        // As on the legacy page, the current financial year unless dates are given
        if (From is null && To is null)
        {
            From = FinancialYear.StartDate(ThisYear);
            To = FinancialYear.EndDate(ThisYear);
        }
        if (From > To)
            (From, To) = (To, From);

        Mode = await chart.GetAccountingModeAsync(Portfolio.PortfolioId);
        var all = await journals.ListAsync(Portfolio.PortfolioId, From, To);
        Journals = Manual ? all.Where(j => j.IsManual).ToList() : all;
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var portfolio = await current.GetSelectedAsync();
        if (portfolio is null)
            return Forbid();

        // Only a manual journal of the selected portfolio is deleted
        var result = await journals.DeleteAsync(portfolio.PortfolioId, id);
        switch (result.Outcome)
        {
            case JournalChangeOutcome.Saved:
                TempData["Message"] = "Journal deleted.";
                break;
            case JournalChangeOutcome.NotManual:
                TempData["Error"] = "Only manual journals can be deleted here. Delete the trade, payout or cash transaction the journal belongs to instead.";
                break;
            default:
                TempData["Error"] = "That journal wasn't found in this portfolio, so nothing was deleted.";
                break;
        }
        return RedirectToPage(new { From, To, Manual });
    }
}
