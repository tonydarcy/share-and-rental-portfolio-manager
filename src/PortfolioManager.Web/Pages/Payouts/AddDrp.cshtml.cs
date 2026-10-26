using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.CorporateActions;
using PortfolioManager.Core.Payouts;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Core.Tax;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Payouts;

public class AddDrpModel(CurrentPortfolio current, PayoutService payouts, CgtDiscountService cgt, AnnouncedActionsService announcedActions, TimeProvider clock) : PageModel
{
    [BindProperty]
    public DrpInput Input { get; set; } = new();

    public PortfolioSummary? Portfolio { get; private set; }

    /// <param name="announced">An announced dividend to start from (Corporate actions > Announced); the units and DRP price need entering unless the DRP price is on file.</param>
    public async Task OnGetAsync(int? announced)
    {
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is not null && announced is { } id
            && await announcedActions.GetDividendAsync(Portfolio.PortfolioId, id, DateOnly.FromDateTime(clock.GetLocalNow().DateTime)) is { } d)
        {
            var p = AnnouncedPayouts.ToPayout(d);
            Input = new DrpInput
            {
                StockExchangeCode = p.StockExchangeCode, StockCode = p.StockCode, Date = p.Date,
                Franked = p.Franked, Unfranked = p.Unfranked, FrankingCredit = p.FrankingCredit, Comment = p.Comment,
            };
            // With the DRP price known (Import > Dividend details), the whole units it buys
            if (AnnouncedPayouts.DrpUnits(d) is { } drp)
                (Input.Units, Input.Price) = drp;
        }
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

        var market = Input.StockExchangeCode.Trim().ToUpperInvariant();
        var code = Input.StockCode.Trim().ToUpperInvariant();
        var held = CgtParcels.Match(await cgt.LoadTradesAsync(Portfolio.PortfolioId), DateOnly.FromDateTime(Input.Date))
            .Open.Where(p => p.ExchangeCode.Equals(market, StringComparison.OrdinalIgnoreCase) && p.StockCode.Equals(code, StringComparison.OrdinalIgnoreCase))
            .Sum(p => p.Units);
        if (held <= 0)
        {
            ModelState.AddModelError(string.Empty, $"No {code} units on {market} were held in this portfolio on {Input.Date:d MMM yyyy}, so there was nothing to pay a dividend on.");
            return Page();
        }

        var result = await payouts.AddDrpAsync(Portfolio.PortfolioId, Input);
        if (!result.Ok)
        {
            ModelState.AddModelError(string.Empty, result.Error!);
            return Page();
        }

        TempData["Message"] = $"Added the {code} DRP: a dividend of {Input.Cash:C} reinvested in {Input.Units:N0} units at {Input.Price:C4}"
            + (Input.Residual == 0 ? "." : $", with {Input.Residual:C} left over.");
        return RedirectToPage("/Payouts/Index");
    }
}
