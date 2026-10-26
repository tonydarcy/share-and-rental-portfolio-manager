using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.CorporateActions;
using PortfolioManager.Core.Payouts;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Core.Prices;
using PortfolioManager.Core.Regions;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Payouts;

public class AddModel(CurrentPortfolio current, PayoutService payouts, AnnouncedActionsService announcedActions, FxRateService fx, TimeProvider clock) : PageModel
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

        var portfolioCurrency = RegionSettings.Current.Currency;
        foreach (var error in Input.Validate().Concat(Input.ValidateCurrency(portfolioCurrency)))
            ModelState.AddModelError(string.Empty, error);
        if (!ModelState.IsValid)
            return Page();

        var foreign = Input.IsForeign(portfolioCurrency);
        if (foreign && Input.ExchangeRate is null)
        {
            // That day's rate from the price feed, as trades do
            if (await fx.GetAsync(Input.Currency!, portfolioCurrency, DateOnly.FromDateTime(Input.Date)) is { } rate)
                Input.ExchangeRate = rate.Rate;
            else
            {
                ModelState.AddModelError(string.Empty, $"There's no {Input.Currency!.Trim().ToUpperInvariant()} exchange rate on file for {Input.Date:d MMM yyyy}. Enter the rate from your statement.");
                return Page();
            }
        }

        await payouts.AddAsync(Portfolio.PortfolioId, Input, portfolioCurrency);
        TempData["Message"] = foreign
            ? $"Added the {Input.StockCode.Trim().ToUpperInvariant()} payout of {Input.Currency!.Trim().ToUpperInvariant()} {Input.Total:N2}, {Input.ToPortfolioCurrency(Input.ExchangeRate!.Value).Total:C} at {Input.ExchangeRate:0.######}."
            : $"Added the {Input.StockCode.Trim().ToUpperInvariant()} payout of {Input.Total:C}.";
        return RedirectToPage("/Payouts/Index");
    }
}
