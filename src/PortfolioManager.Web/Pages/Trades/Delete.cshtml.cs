using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Trades;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Trades;

/// <summary>Confirmation step for deleting a trade. The GET only shows it; the POST deletes.</summary>
public class DeleteModel(CurrentPortfolio current, TradesService trades) : PageModel
{
    public ShareTrade? Trade { get; private set; }
    public TradeDeleteCheck? Check { get; private set; }
    public string? BlockedReason { get; private set; }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var portfolio = await current.GetSelectedAsync();
        if (portfolio is null)
            return RedirectToPage("./Index");

        // Only trades in the selected portfolio
        Trade = await trades.GetTradeAsync(portfolio.PortfolioId, id);
        if (Trade is null)
            return NotFound();

        Check = await trades.CheckDeleteAsync(portfolio.PortfolioId, id);
        BlockedReason = TradesService.BlockReason(Check);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int id)
    {
        var portfolio = await current.GetSelectedAsync();
        if (portfolio is null)
            return RedirectToPage("./Index");

        // The service re-checks that the trade belongs to this portfolio inside the delete transaction
        var result = await trades.DeleteTradeAsync(portfolio.PortfolioId, id);
        switch (result.Outcome)
        {
            case TradeDeleteOutcome.NotFound:
                return NotFound();
            case TradeDeleteOutcome.Blocked:
                Trade = await trades.GetTradeAsync(portfolio.PortfolioId, id);
                if (Trade is null)
                    return NotFound();
                Check = await trades.CheckDeleteAsync(portfolio.PortfolioId, id);
                BlockedReason = result.Reason;
                return Page();
            default:
                TempData["Message"] = $"Trade {id} was deleted, along with any cash transaction and journal entries recorded with it.";
                return RedirectToPage("./Index");
        }
    }
}
