using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Payouts;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Payouts;

public class EditModel(CurrentPortfolio current, PayoutService payouts) : PageModel
{
    [BindProperty]
    public PayoutInput Input { get; set; } = new();

    public bool AmountsLocked { get; private set; }

    /// <summary>What a payout in another currency was entered as.</summary>
    public string? EnteredAs { get; private set; }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var (portfolioId, record) = await LoadAsync(id);
        if (record is null)
            return NotFound();
        if (record.IsRental)
            return RedirectToPage("/Payouts/EditRental", new { id });

        Input = PayoutEdit.FromRecord(record);
        AmountsLocked = record.ReinvestmentTradeId is not null;
        EnteredAs = record.EnteredAs;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int id)
    {
        var (portfolioId, record) = await LoadAsync(id);
        if (record is null)
            return NotFound();
        if (record.IsRental)
            return RedirectToPage("/Payouts/EditRental", new { id });

        // A DRP payout's code and amounts belong with its reinvestment trade
        AmountsLocked = record.ReinvestmentTradeId is not null;
        var (keepAmounts, errors) = PayoutEdit.Prepare(record, Input);
        foreach (var error in errors)
            ModelState.AddModelError(string.Empty, error);
        if (!ModelState.IsValid)
            return Page();

        if (!await payouts.UpdateAsync(portfolioId, id, Input, keepAmounts))
            return NotFound();

        TempData["Message"] = $"Updated the {Input.StockCode} payout.";
        return RedirectToPage("/Payouts/Index");
    }

    /// <summary>The payout, only when it belongs to the selected portfolio.</summary>
    private async Task<(int PortfolioId, PayoutRecord? Record)> LoadAsync(int id)
    {
        var portfolio = await current.GetSelectedAsync();
        if (portfolio is null)
            return (0, null);
        return (portfolio.PortfolioId, await payouts.GetAsync(portfolio.PortfolioId, id));
    }
}
