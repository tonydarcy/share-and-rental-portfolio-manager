using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.CorporateActions;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.CorporateActions;

public class IndexModel(CurrentPortfolio current, CorporateActionsService actions, ParcelTransferService transfers) : PageModel
{
    public PortfolioSummary? Portfolio { get; private set; }
    public IReadOnlyList<SplitRecord> Splits { get; private set; } = [];
    public IReadOnlyList<CapitalReturnRecord> CapitalReturns { get; private set; } = [];
    public IReadOnlyList<ParcelTransferRecord> Transfers { get; private set; } = [];

    public async Task OnGetAsync()
    {
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is null)
            return;
        Splits = await actions.GetSplitsAsync(Portfolio.PortfolioId);
        CapitalReturns = await actions.GetCapitalReturnsAsync(Portfolio.PortfolioId);
        Transfers = (await actions.GetParcelTransfersAsync(Portfolio.PortfolioId)).Reverse().ToList();
    }

    public async Task<IActionResult> OnPostDeleteSplitAsync(int id)
    {
        var portfolio = await current.GetSelectedAsync();
        if (portfolio is null)
            return Forbid();

        // Only a split recorded for the selected portfolio
        TempData["Message"] = await actions.DeleteSplitAsync(portfolio.PortfolioId, id) switch
        {
            null => "That split wasn't found in this portfolio, so nothing was changed.",
            { Ok: true } => "Split deleted. The trades before it are back in the old units.",
            { Error: var error } => $"The split wasn't deleted: {error}",
        };
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteReturnAsync(int id)
    {
        var portfolio = await current.GetSelectedAsync();
        if (portfolio is null)
            return Forbid();

        // Only a return of capital recorded for the selected portfolio
        TempData["Message"] = await actions.DeleteCapitalReturnAsync(portfolio.PortfolioId, id) switch
        {
            null => "That return of capital wasn't found in this portfolio, so nothing was changed.",
            { Ok: true } => "Return of capital deleted, with its journal.",
            { Error: var error } => $"The return of capital wasn't deleted: {error}",
        };
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteTransferAsync(int id)
    {
        var portfolio = await current.GetSelectedAsync();
        if (portfolio is null)
            return Forbid();

        // Only one recorded for the selected portfolio
        TempData["Message"] = await transfers.DeleteAsync(portfolio.PortfolioId, id) switch
        {
            null => "That corporate action wasn't found in this portfolio, so nothing was changed.",
            { Ok: true } => "Corporate action deleted, with the trades and journal it added.",
            { Error: var error } => $"The corporate action wasn't deleted: {error}",
        };
        return RedirectToPage();
    }
}
