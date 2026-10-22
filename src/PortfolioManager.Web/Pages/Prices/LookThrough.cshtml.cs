using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Holdings;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Core.Reports;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Prices;

/// <summary>What is inside the portfolio's ETFs and funds, for the look-through on Reports > Diversity.</summary>
public class LookThroughModel(CurrentPortfolio current, LookThroughService lookThrough, HoldingsService holdings) : PageModel
{
    /// <summary>"exchange:code".</summary>
    [BindProperty(SupportsGet = true)] public string? Fund { get; set; }
    [BindProperty] public string? Weights { get; set; }
    [BindProperty] public DateOnly? AsAt { get; set; }

    [TempData] public string? Success { get; set; }

    public PortfolioSummary? Portfolio { get; private set; }
    public IReadOnlyList<Holding> Held { get; private set; } = [];
    public IReadOnlyDictionary<(string, string), FundExposure> Funds { get; private set; } = new Dictionary<(string, string), FundExposure>();
    public FundExposure? Selected { get; private set; }
    public string? LoadError { get; private set; }

    public async Task OnGetAsync()
    {
        if (!await LoadAsync() || Key() is not { } key)
            return;
        Selected = Funds.GetValueOrDefault(key);
        Weights = Selected is null ? "" : LookThrough.ToText(Selected.Weights);
        AsAt = Selected?.AsAt;
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        if (!await LoadAsync() || Key() is not { } key)
            return Page();
        var (weights, errors) = LookThrough.Parse(Weights);
        if (weights.Count == 0 && errors.Count == 0)
            errors = ["Enter at least one line."];
        foreach (var e in errors)
            ModelState.AddModelError(nameof(Weights), e);
        if (errors.Count > 0)
            return Page();
        var result = await lookThrough.SaveAsync(Portfolio!.PortfolioId, key.Item1, key.Item2, weights, AsAt);
        if (!result.Ok)
        {
            ModelState.AddModelError(nameof(Weights), result.Error!);
            return Page();
        }
        Success = $"Look-through for {key.Item2} saved.";
        return RedirectToPage(new { fund = Fund });
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        if (!await LoadAsync() || Key() is not { } key)
            return Page();
        await lookThrough.DeleteAsync(Portfolio!.PortfolioId, key.Item1, key.Item2);
        Success = $"Your look-through for {key.Item2} was removed.";
        return RedirectToPage(new { fund = Fund });
    }

    private (string, string)? Key() =>
        Fund?.Split(':', 2) is [var ex, var code] && Held.Any(h => h.ExchangeCode.Trim().Equals(ex, StringComparison.OrdinalIgnoreCase)
            && h.StockCode.Trim().Equals(code, StringComparison.OrdinalIgnoreCase))
            ? (ex.Trim().ToUpperInvariant(), code.Trim().ToUpperInvariant()) : null;

    private async Task<bool> LoadAsync()
    {
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is null)
            return false;
        Held = (await holdings.GetHoldingsAsync(Portfolio.PortfolioId, null, true)).Holdings.Where(h => h.Units != 0).OrderBy(h => h.StockCode).ToList();
        try
        {
            Funds = await lookThrough.GetFundsAsync(Portfolio.PortfolioId);
            return true;
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208)
        {
            LoadError = "Look-through needs the database update 2026-10-21-06-fund-look-through.sql. Ask whoever runs this site to apply it.";
            return false;
        }
    }
}
