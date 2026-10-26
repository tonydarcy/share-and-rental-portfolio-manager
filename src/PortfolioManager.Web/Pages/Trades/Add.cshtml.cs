using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Cash;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Core.Prices;
using PortfolioManager.Core.Trades;
using PortfolioManager.Core.Validation;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Trades;

public class AddModel(CurrentPortfolio current, TradeService trades, CashService cash, FxRateService fx) : PageModel
{
    [BindProperty]
    public TradeForm Input { get; set; } = new();

    [TempData]
    public string? Success { get; set; }

    public PortfolioSummary? Portfolio { get; private set; }
    public string PortfolioCurrency { get; private set; } = "AUD";
    public IReadOnlyList<BankAccount> BankAccounts { get; private set; } = [];

    /// <summary>The query string can fill the form, e.g. from a contract note read on Business > Read a receipt or bill.</summary>
    public async Task OnGetAsync(string? code, string? type, decimal? qty, decimal? price, decimal? brokerage, DateOnly? date)
    {
        if (!await LoadAsync())
            return;
        Input = new TradeForm
        {
            Date = date ?? DateOnly.FromDateTime(DateTime.Now),
            Type = type == "Sell" ? "Sell" : "Buy",
            Code = code?.Trim().ToUpperInvariant(),
            Quantity = qty,
            Price = price,
            Market = "ASX",
            Currency = PortfolioCurrency,
            ExchangeRate = 1m,
            Brokerage = brokerage ?? 0m,
            RecordCash = true,
        };
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!await LoadAsync())
            return Page();

        // A foreign trade with no rate (or 1) takes the FX rate on file for its date, so its cost base is in the portfolio currency
        string? fxNote = null;
        if (Input.Currency is { Length: 3 } cur && !cur.Equals(PortfolioCurrency, StringComparison.OrdinalIgnoreCase)
            && Input.ExchangeRate is null or 1m && Input.Date is { } day
            && await fx.GetAsync(cur, PortfolioCurrency, day) is { } rate)
        {
            Input.ExchangeRate = rate.Rate;
            ModelState.Remove($"{nameof(Input)}.{nameof(Input.ExchangeRate)}");
            fxNote = $" The exchange rate {rate.Rate:0.######} {cur} per {PortfolioCurrency} is the FX close on {rate.Date:d MMM yyyy}.";
        }

        var result = TradeEntry.Validate(Input, FormRules.LatestToday(DateTime.UtcNow));
        var errors = result.Errors.ToList();
        if (result.Value is { } t && TradeEntry.CheckSettlementAccount(t, PortfolioCurrency, BankAccounts) is { } accountError)
            errors.Add(accountError);

        foreach (var e in errors)
        {
            // Keep the binder's own message (e.g. "not a number") rather than adding a second one
            var key = $"{nameof(Input)}.{e.Field}";
            if (ModelState[key] is not { Errors.Count: > 0 })
                ModelState.AddModelError(key, e.Message);
        }
        if (!ModelState.IsValid || result.Value is not { } trade || errors.Count > 0)
            return Page();

        await trades.AddAsync(Portfolio!.PortfolioId, trade);

        Success = $"{trade.TypeText} of {trade.Quantity:#,##0.########} {trade.Code} on {trade.Date:d} recorded"
            + (trade.Currency.Equals(PortfolioCurrency, StringComparison.OrdinalIgnoreCase)
                ? $" ({trade.OrderTotal:C})."
                : $" ({trade.OrderTotal:N2} {trade.Currency}, {trade.PortfolioCurrencyTotal:C} {PortfolioCurrency}).")
            + (trade.Cash is null ? "" : " The matching cash transaction was added too.")
            + fxNote;
        return RedirectToPage();
    }

    private async Task<bool> LoadAsync()
    {
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is null)
            return false;
        PortfolioCurrency = await cash.GetPortfolioCurrencyAsync(Portfolio.PortfolioId);
        BankAccounts = await cash.GetBankAccountsAsync(Portfolio.PortfolioId);
        return true;
    }
}
