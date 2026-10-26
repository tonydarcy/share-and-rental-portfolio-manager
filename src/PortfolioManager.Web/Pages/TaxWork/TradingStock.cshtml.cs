using Microsoft.AspNetCore.Mvc;
using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Holdings;
using PortfolioManager.Core.Tax;
using PortfolioManager.Core.TaxWork;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.TaxWork;

/// <summary>Shares held as trading stock (a share trader): the setting, and the year's opening and closing stock and trading profit.</summary>
public class TradingStockModel(CurrentPortfolio current, TaxWorkpaperService workpapers, LockedPeriodService locks, TradingStockService trading,
    HoldingsService holdings) : TaxYearPageModel(current, workpapers, locks)
{
    public sealed class SettingsForm
    {
        public bool Trader { get; set; }
        public int? FromYear { get; set; }
        public string Valuation { get; set; } = TradingStockValuation.Cost;
    }

    [BindProperty] public SettingsForm Input { get; set; } = new();

    public ShareTraderSettings Settings { get; private set; } = ShareTraderSettings.None;
    /// <summary>Every holding the portfolio has had, as "exchange:code", for choosing which are investments.</summary>
    public IReadOnlyList<(string Exchange, string Code)> Codes { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync()
    {
        if (await LoadAsync() is { } refusal)
            return refusal;
        if (Portfolio is not null)
        {
            Settings = await trading.GetSettingsAsync(Portfolio.PortfolioId);
            Input = new SettingsForm { Trader = Settings.FromYear is not null, FromYear = Settings.FromYear ?? Year, Valuation = Settings.Valuation };
            Codes = (await holdings.GetHoldingsAsync(Portfolio.PortfolioId, null, false)).Holdings
                .Select(h => TradingStock.Key(h.ExchangeCode, h.StockCode)).Distinct().OrderBy(k => k.Item2).ToList();
        }
        return Page();
    }

    /// <summary>Keeps one holding as an investment, or makes it trading stock again.</summary>
    public async Task<IActionResult> OnPostHoldingAsync(string fund, bool investment)
    {
        if (await LoadAsync() is { } refusal)
            return refusal;
        if (fund?.Split(':', 2) is not [var ex, var code])
            return RedirectToPage(new { fy = Year });
        var result = await trading.SetInvestmentAsync(Portfolio!.PortfolioId, ex, code, investment);
        return Done(result, investment ? $"{code.ToUpperInvariant()} is kept as an investment: its gains are capital gains."
            : $"{code.ToUpperInvariant()} is trading stock.");
    }

    /// <summary>Posts the year's trading stock journal to the ledger.</summary>
    public async Task<IActionResult> OnPostLedgerAsync()
    {
        if (await LoadAsync() is { } refusal)
            return refusal;
        var result = await trading.PostLedgerAsync(Portfolio!.PortfolioId, Year);
        return Done(result, $"The {FinancialYear.Label(Year)} trading stock journal is posted at the year end.");
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
