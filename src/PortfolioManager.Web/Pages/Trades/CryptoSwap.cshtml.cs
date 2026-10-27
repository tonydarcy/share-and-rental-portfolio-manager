using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Cash;
using PortfolioManager.Core.Crypto;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Core.Tax;
using PortfolioManager.Core.Validation;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Trades;

/// <summary>One coin swapped for another, recorded as a Sell and a Buy at the same value.</summary>
public class CryptoSwapModel(CurrentPortfolio current, CryptoService crypto, CashService cash, CgtDiscountService cgt) : PageModel
{
    [BindProperty]
    public SwapForm Input { get; set; } = new();

    [TempData]
    public string? Success { get; set; }

    public PortfolioSummary? Portfolio { get; private set; }
    public string PortfolioCurrency { get; private set; } = "AUD";
    /// <summary>Coins held today, for the coin boxes' suggestions.</summary>
    public IReadOnlyList<(string Code, decimal Units)> Coins { get; private set; } = [];

    public async Task OnGetAsync()
    {
        if (await LoadAsync())
            Input = new SwapForm { Date = DateOnly.FromDateTime(DateTime.Now) };
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!await LoadAsync())
            return Page();

        var result = CryptoEntry.Validate(Input, PortfolioCurrency, FormRules.LatestToday(DateTime.UtcNow));
        foreach (var e in result.Errors)
        {
            var key = $"{nameof(Input)}.{e.Field}";
            if (ModelState[key] is not { Errors.Count: > 0 })
                ModelState.AddModelError(key, e.Message);
        }
        if (!ModelState.IsValid || result.Value is not { } swap)
            return Page();

        var held = await HeldAsync(swap.FromCode, swap.Date);
        if (held < swap.FromUnits)
        {
            ModelState.AddModelError($"{nameof(Input)}.{nameof(Input.FromUnits)}",
                $"Only {held:#,##0.########} {swap.FromCode} were held on {swap.Date:d MMM yyyy}. Add the purchases first, or check the units.");
            return Page();
        }

        await crypto.AddSwapAsync(Portfolio!.PortfolioId, swap);
        Success = $"Recorded the swap of {swap.FromUnits:#,##0.########} {swap.FromCode} for {swap.ToUnits:#,##0.########} {swap.ToCode} " +
            $"on {swap.Date:d MMM yyyy} at {swap.Value:C}: a sale of {swap.FromCode} and a purchase of {swap.ToCode}.";
        return RedirectToPage();
    }

    private async Task<decimal> HeldAsync(string code, DateOnly date) =>
        CgtParcels.Match(await cgt.LoadTradesAsync(Portfolio!.PortfolioId), date).Open
            .Where(p => CryptoCodes.IsCrypto(p.ExchangeCode) && p.StockCode.Equals(code, StringComparison.OrdinalIgnoreCase))
            .Sum(p => p.Units);

    private async Task<bool> LoadAsync()
    {
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is null)
            return false;
        PortfolioCurrency = await cash.GetPortfolioCurrencyAsync(Portfolio.PortfolioId);
        Coins = CgtParcels.Match(await cgt.LoadTradesAsync(Portfolio.PortfolioId), DateOnly.FromDateTime(DateTime.Now)).Open
            .Where(p => CryptoCodes.IsCrypto(p.ExchangeCode))
            .GroupBy(p => p.StockCode.Trim().ToUpperInvariant())
            .Select(g => (g.Key, g.Sum(p => p.Units)))
            .OrderBy(c => c.Key)
            .ToList();
        return true;
    }
}
