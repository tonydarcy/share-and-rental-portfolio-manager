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

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var (portfolioId, record) = await LoadAsync(id);
        if (record is null)
            return NotFound();
        if (record.IsRental)
            return RedirectToPage("/Payouts/EditRental", new { id });

        Input = FromRecord(record);
        AmountsLocked = record.ReinvestmentTradeId is not null;
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
        var stored = FromRecord(record);
        if (AmountsLocked)
        {
            Input.StockCode = stored.StockCode;
            Input.StockExchangeCode = stored.StockExchangeCode;
        }

        // Old payouts whose figures don't add up can still have their code, date or comment changed
        var keepAmounts = AmountsLocked || SameAmounts(stored, Input);
        if (keepAmounts)
            CopyAmounts(stored, Input);

        var errors = Input.ValidateDetails().ToList();
        if (!AmountsLocked)
            errors.AddRange(Input.ValidateCode());
        if (!keepAmounts)
            errors.AddRange(Input.ValidateAmounts());
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

    private static PayoutInput FromRecord(PayoutRecord r) => new()
    {
        StockCode = r.StockCode ?? "",
        StockExchangeCode = r.StockExchangeCode ?? "",
        Date = r.Date ?? DateTime.Today,
        Total = Money(r.Total),
        Net = r.Net is null ? null : Money(r.Net),
        Franked = Money(r.Franked),
        Unfranked = Money(r.Unfranked),
        Interest = Money(r.Interest),
        TaxWithheld = Money(r.TaxWithheld),
        ForeignTax = Money(r.ForeignTax),
        Other = Money(r.Other),
        FrankingCredit = Money(r.FrankingCredit),
        TrustIncome = Money(r.TrustIncome),
        Comment = r.Comment,
        Confirmed = string.Equals(r.Confirmed?.Trim(), "Yes", StringComparison.OrdinalIgnoreCase),
    };

    private static decimal Money(double? v) => Math.Round((decimal)(v ?? 0), 2);

    private static void CopyAmounts(PayoutInput from, PayoutInput to)
    {
        to.Total = from.Total;
        to.Net = from.Net;
        to.Franked = from.Franked;
        to.Unfranked = from.Unfranked;
        to.Interest = from.Interest;
        to.TaxWithheld = from.TaxWithheld;
        to.ForeignTax = from.ForeignTax;
        to.Other = from.Other;
        to.FrankingCredit = from.FrankingCredit;
        to.TrustIncome = from.TrustIncome;
    }

    private static bool SameAmounts(PayoutInput a, PayoutInput b) =>
        a.Total == b.Total && a.Franked == b.Franked && a.Unfranked == b.Unfranked && a.Interest == b.Interest
        && a.TaxWithheld == b.TaxWithheld && a.ForeignTax == b.ForeignTax && a.Other == b.Other
        && a.FrankingCredit == b.FrankingCredit && a.TrustIncome == b.TrustIncome;
}
