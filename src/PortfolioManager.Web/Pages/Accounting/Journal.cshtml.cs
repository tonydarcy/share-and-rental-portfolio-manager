using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.SalesTax;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Accounting;

public class JournalModel(CurrentPortfolio current, JournalService journals, RecurringJournalService repeating, ChartOfAccountsService chart, SalesTaxService salesTax) : PageModel
{
    public PortfolioSummary? Portfolio { get; private set; }
    public JournalDetail? Journal { get; private set; }
    public IReadOnlyDictionary<int, int> AccountNumbers { get; private set; } = new Dictionary<int, int>();
    public IReadOnlyDictionary<int, string> TaxCodes { get; private set; } = new Dictionary<int, string>();

    public async Task<IActionResult> OnGetAsync(int id)
    {
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is null)
            return Forbid();

        // Null unless the journal belongs to the selected portfolio
        Journal = await journals.GetAsync(Portfolio.PortfolioId, id);
        if (Journal is null)
        {
            TempData["Error"] = "That journal wasn't found in this portfolio.";
            return RedirectToPage("/Accounting/Journals");
        }
        AccountNumbers = (await chart.GetAccountsAsync(Portfolio.PortfolioId)).ToDictionary(a => a.AccountId, a => a.Number);
        TaxCodes = (await salesTax.GetCodesAsync(Portfolio.PortfolioId)).ToDictionary(c => c.TaxCodeId, c => c.Code);
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var portfolio = await current.GetSelectedAsync();
        if (portfolio is null)
            return Forbid();

        var result = await journals.DeleteAsync(portfolio.PortfolioId, id);
        if (result.Outcome == JournalChangeOutcome.Saved)
        {
            TempData["Message"] = "Journal deleted.";
            return RedirectToPage("/Accounting/Journals");
        }
        TempData["Error"] = result.Outcome == JournalChangeOutcome.NotManual
            ? "Only manual journals can be deleted here."
            : "That journal wasn't found in this portfolio, so nothing was deleted.";
        return RedirectToPage("/Accounting/Journals");
    }

    /// <summary>Posts the same lines the other way round on the date chosen (e.g. an accrual reversing on 1 July).</summary>
    public async Task<IActionResult> OnPostReverseAsync(int id, DateTime? date)
    {
        var portfolio = await current.GetSelectedAsync();
        if (portfolio is null)
            return Forbid();
        if (date is null)
        {
            TempData["Error"] = "Choose the date to reverse it on.";
            return RedirectToPage(new { id });
        }
        var codes = (await salesTax.GetCodesAsync(portfolio.PortfolioId)).ToDictionary(c => c.TaxCodeId);
        var result = await repeating.ReverseAsync(portfolio.PortfolioId, id, DateOnly.FromDateTime(date.Value), codes);
        if (result.Outcome == JournalChangeOutcome.Saved)
        {
            TempData["Message"] = "Reversal posted.";
            return RedirectToPage(new { id = result.JournalId });
        }
        TempData["Error"] = result.Outcome switch
        {
            JournalChangeOutcome.NotManual => "Only manual journals can be reversed here.",
            JournalChangeOutcome.NotFound => "That journal wasn't found in this portfolio.",
            _ => string.Join(" ", result.Errors ?? []),
        };
        return RedirectToPage(new { id });
    }
}
