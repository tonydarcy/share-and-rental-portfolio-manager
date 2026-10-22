using Microsoft.AspNetCore.Mvc;
using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Tax;
using PortfolioManager.Core.TaxWork;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.TaxWork;

/// <summary>Shares held as trading stock (a share trader): the setting, and the year's opening and closing stock and trading profit.</summary>
public class TradingStockModel(CurrentPortfolio current, TaxWorkpaperService workpapers, LockedPeriodService locks, TradingStockService trading)
    : TaxYearPageModel(current, workpapers, locks)
{
    public sealed class SettingsForm
    {
        public bool Trader { get; set; }
        public int? FromYear { get; set; }
        public string Valuation { get; set; } = TradingStockValuation.Cost;
    }

    [BindProperty] public SettingsForm Input { get; set; } = new();

    public ShareTraderSettings Settings { get; private set; } = ShareTraderSettings.None;

    public async Task<IActionResult> OnGetAsync()
    {
        if (await LoadAsync() is { } refusal)
            return refusal;
        if (Portfolio is not null)
        {
            Settings = await trading.GetSettingsAsync(Portfolio.PortfolioId);
            Input = new SettingsForm { Trader = Settings.FromYear is not null, FromYear = Settings.FromYear ?? Year, Valuation = Settings.Valuation };
        }
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (await LoadAsync() is { } refusal)
            return refusal;
        if (!TradingStockValuation.All.Contains(Input.Valuation) || (Input.Trader && Input.FromYear is not (>= 1990 and <= 2100)))
        {
            TempData["Error"] = "Choose the first year and how the shares are valued.";
            return RedirectToPage(new { fy = Year });
        }
        // Saved for the selected portfolio only
        var result = await trading.SaveSettingsAsync(Portfolio!.PortfolioId,
            new ShareTraderSettings(Input.Trader ? Input.FromYear : null, Input.Valuation));
        return Done(result, Input.Trader
            ? $"The shares are trading stock from {FinancialYear.Label(Input.FromYear!.Value)}, valued at {TradingStockValuation.Text(Input.Valuation).ToLowerInvariant()}."
            : "The shares are investments again: their gains are capital gains.");
    }
}
