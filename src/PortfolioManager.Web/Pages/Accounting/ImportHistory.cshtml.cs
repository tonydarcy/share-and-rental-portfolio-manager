using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Accounting;

public class ImportHistoryModel(CurrentPortfolio current, BankImportService imports) : PageModel
{
    public PortfolioSummary? Portfolio { get; private set; }
    public IReadOnlyList<ImportBatch> Batches { get; private set; } = [];

    public async Task OnGetAsync()
    {
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is null)
            return;
        Batches = await imports.GetHistoryAsync(Portfolio.PortfolioId);
    }

    public async Task<IActionResult> OnPostDeleteAsync(string batchId)
    {
        var portfolio = await current.GetSelectedAsync();
        if (portfolio is null)
            return Forbid();

        // Only a batch of the selected portfolio is deleted, and only while none of it is allocated
        var result = await imports.DeleteBatchAsync(portfolio.PortfolioId, batchId ?? "");
        if (result.Ok)
            TempData["Message"] = "Import deleted.";
        else
            TempData["Error"] = result.Error;
        return RedirectToPage();
    }
}
