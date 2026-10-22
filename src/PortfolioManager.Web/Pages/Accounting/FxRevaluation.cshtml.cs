using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Data;
using PortfolioManager.Core.Prices;
using PortfolioManager.Core.Regions;
using PortfolioManager.Web.Pages.Shared;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Accounting;

/// <summary>
/// Revalues open foreign currency invoices, bills and accounts at a date's rates, posting the unrealised gain or loss.
/// Invoice and bill revaluations reverse the next day; the accounts' revaluation stays.
/// </summary>
public class FxRevaluationModel(CurrentPortfolio current, ForeignCurrencyService fx, FxRateService fxRates, ILogger<FxRevaluationModel> logger)
    : BooksPageModel(current, logger)
{
    [BindProperty(SupportsGet = true)] public DateOnly? Date { get; set; }

    /// <summary>Rates by currency, as posted (rate_USD and so on).</summary>
    [BindProperty] public Dictionary<string, string?> Rates { get; set; } = [];

    public IReadOnlyList<FxOpenItem> Items { get; private set; } = [];
    public IReadOnlyDictionary<string, decimal> RateFor { get; private set; } = new Dictionary<string, decimal>();
    public IReadOnlyList<FxRevaluation> History { get; private set; } = [];
    public DateOnly AsAt => Date ?? Today;

    public async Task OnGetAsync()
    {
        if (!await LoadAsync())
            return;
        // Default each currency's rate to the latest on file
        var found = new Dictionary<string, decimal>();
        foreach (var c in Items.Select(i => i.Currency).Distinct())
            if (await fxRates.GetAsync(c, RegionSettings.Current.Currency, AsAt) is { } r)
                found[c] = r.Rate;
        RateFor = found;
    }

    public async Task<IActionResult> OnPostPostAsync()
    {
        if (!await LoadAsync())
            return Forbid();
        var rates = new Dictionary<string, decimal>();
        foreach (var c in Items.Select(i => i.Currency).Distinct())
        {
            if (!decimal.TryParse(Rates.GetValueOrDefault(c), NumberStyles.Number, CultureInfo.InvariantCulture, out var rate) || rate <= 0)
                return Done(ProcResult.Failed($"Enter the {c} rate."), "", new { date = AsAt.ToString("yyyy-MM-dd") });
            rates[c] = rate;
        }
        return Done(await RunAsync(() => fx.RevalueAsync(PortfolioId, AsAt, rates)), "Revaluation posted.", new { date = AsAt.ToString("yyyy-MM-dd") });
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        if (!await LoadAsync())
            return Forbid();
        return Done(await RunAsync(() => fx.DeleteRevaluationAsync(PortfolioId, id)), "Revaluation deleted.");
    }

    private async Task<bool> LoadAsync()
    {
        if (!await LoadPortfolioAsync())
            return false;
        Items = await fx.GetOpenItemsAsync(PortfolioId, AsAt);
        History = await fx.GetRevaluationsAsync(PortfolioId);
        return true;
    }
}
