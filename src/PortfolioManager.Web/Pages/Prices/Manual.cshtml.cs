using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Core.Prices;
using PortfolioManager.Core.Trades;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Prices;

[RequestSizeLimit(CsvUpload.RequestLimit)]
[RequestFormLimits(MultipartBodyLengthLimit = CsvUpload.RequestLimit)]
public class ManualModel(CurrentPortfolio current, ManualPriceService prices, TradesService trades) : PageModel
{
    public PortfolioSummary? Portfolio { get; private set; }

    /// <summary>The portfolio's holdings on markets with no price feed, as "market:code".</summary>
    public IReadOnlyList<string> Holdings { get; private set; } = [];
    public IReadOnlyList<ManualPrice> Prices { get; private set; } = [];
    public string? LoadError { get; private set; }

    [BindProperty] public string? Holding { get; set; }
    [BindProperty] public DateOnly? Date { get; set; }
    [BindProperty] public decimal? Price { get; set; }
    [BindProperty] public string? Market { get; set; }
    [BindProperty] public IFormFile? Upload { get; set; }

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostAddAsync()
    {
        if (!await LoadAsync())
            return Forbid();
        if (Holding?.Split(':', 2) is not [var market, var code] || !Holdings.Contains(Holding))
            ModelState.AddModelError(string.Empty, "Choose a holding.");
        else if (Date is null || Price is not > 0)
            ModelState.AddModelError(string.Empty, "Enter the date and a price above zero.");
        else
        {
            var result = await prices.SaveAsync(Portfolio!.PortfolioId, market, code, Date.Value, Price.Value);
            if (result.Ok)
            {
                TempData["Message"] = $"Saved {code} at {Price:N4} on {Date:d MMM yyyy}.";
                return RedirectToPage();
            }
            ModelState.AddModelError(string.Empty, result.Error ?? "Not saved.");
        }
        return Page();
    }

    public async Task<IActionResult> OnPostUploadAsync()
    {
        if (!await LoadAsync())
            return Forbid();
        var (text, error) = await CsvUpload.ReadAsync(Upload);
        if (error is not null || string.IsNullOrWhiteSpace(Market) || !AssetTypes.IsManual(Market))
        {
            ModelState.AddModelError(string.Empty, error ?? "Choose the kind of asset the prices are for.");
            return Page();
        }
        var preview = ManualPriceService.Parse(text!, DateOnly.FromDateTime(DateTime.Today));
        if (preview.FileError is not null || preview.ErrorCount > 0)
        {
            ModelState.AddModelError(string.Empty, preview.FileError ?? string.Join(" ", preview.Lines.Where(l => !l.IsValid)
                .Take(5).Select(l => $"Line {l.LineNumber}: {string.Join(" ", l.Errors)}")) + " Nothing was saved.");
            return Page();
        }
        var market = Market.Trim().ToUpperInvariant();
        var unknown = preview.ValidRows.Select(r => r.StockCode).Distinct().Where(c => !Holdings.Contains($"{market}:{c}")).ToList();
        if (unknown.Count > 0)
        {
            ModelState.AddModelError(string.Empty, $"No {market} trades for {string.Join(", ", unknown.Take(5))} in this portfolio. Nothing was saved.");
            return Page();
        }
        var saved = 0;
        foreach (var r in preview.ValidRows)
            if ((await prices.SaveAsync(Portfolio!.PortfolioId, market, r.StockCode, r.Date, r.Price)).Ok)
                saved++;
        TempData["Message"] = $"Saved {saved} price{(saved == 1 ? "" : "s")}.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        if (!await LoadAsync())
            return Forbid();
        var result = await prices.DeleteAsync(Portfolio!.PortfolioId, id);
        TempData["Message"] = result.Ok ? "Price deleted." : result.Error;
        return RedirectToPage();
    }

    private async Task<bool> LoadAsync()
    {
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is null)
            return false;
        Holdings = (await trades.GetTradesAsync(Portfolio.PortfolioId, null, oldestFirst: true))
            .Where(t => AssetTypes.IsManual(t.ExchangeCode))
            .Select(t => $"{t.ExchangeCode.Trim()}:{t.StockCode.Trim()}".ToUpperInvariant())
            .Distinct().Order().ToList();
        try
        {
            Prices = await prices.GetAsync(Portfolio.PortfolioId);
        }
        catch (Microsoft.Data.SqlClient.SqlException)
        {
            LoadError = "Manual prices need Database/Migrations/2026-10-21-04-manual-prices.sql to be run first.";
        }
        return true;
    }
}
