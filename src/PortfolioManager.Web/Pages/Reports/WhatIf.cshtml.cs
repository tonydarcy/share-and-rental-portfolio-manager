using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Core.Regions;
using PortfolioManager.Core.Tax;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Reports;

/// <summary>Nothing is saved, so it's a GET form and works with read-only access too.</summary>
public class WhatIfModel(CurrentPortfolio current, CgtDiscountService cgt, WhatIfService whatIf) : PageModel
{
    /// <summary>"exchange:code".</summary>
    [BindProperty(SupportsGet = true)] public string? Holding { get; set; }
    [BindProperty(SupportsGet = true)] public decimal? Units { get; set; }
    [BindProperty(SupportsGet = true)] public decimal? Price { get; set; }
    [BindProperty(SupportsGet = true)] public decimal? Brokerage { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? Date { get; set; }
    [BindProperty(SupportsGet = true)] public ParcelMethod Method { get; set; } = ParcelMethod.Fifo;
    /// <summary>Percent, e.g. 32.</summary>
    [BindProperty(SupportsGet = true)] public decimal? TaxRate { get; set; }

    public PortfolioSummary? Portfolio { get; private set; }
    public RegionSettings Region { get; private set; } = RegionSettings.Australia;
    public CgtDiscountRate Rate { get; private set; } = CgtDiscountRate.None;
    public IReadOnlyList<(string Key, decimal Units)> Holdings { get; private set; } = [];
    public WhatIfResult? Result { get; private set; }
    public DateOnly? PriceDate { get; private set; }

    public async Task OnGetAsync()
    {
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is null)
            return;
        Region = await current.GetRegionAsync();
        Rate = CgtDiscount.RateFor(Portfolio.EntityType, Region);
        var today = PeriodReportModel.Today;
        Holdings = (await cgt.GetOpenParcelsAsync(Portfolio.PortfolioId, today)).Open
            .GroupBy(p => $"{p.ExchangeCode}:{p.StockCode}".ToUpperInvariant())
            .Select(g => (g.Key, g.Sum(p => p.Units)))
            .OrderBy(h => h.Key[(h.Key.IndexOf(':') + 1)..])
            .ToList();

        TaxRate ??= WhatIf.DefaultTaxRate(Rate) * 100;
        Date ??= today;
        if (Holding is null || Holding.Split(':', 2) is not [var exchange, var code])
            return;

        var held = Holdings.FirstOrDefault(h => h.Key == Holding.ToUpperInvariant()).Units;
        Units ??= held;
        if (Price is null && await whatIf.LatestPriceAsync(Portfolio.PortfolioId, exchange, code) is { } latest)
        {
            Price = latest.Price;
            PriceDate = latest.Date;
        }
        if (Price is null)
            return;

        var sale = new PlannedSale(exchange, code, Date.Value, Units ?? 0, Price.Value, Brokerage ?? 0,
            Method == ParcelMethod.Specific ? ParcelMethod.Fifo : Method);
        Result = await whatIf.RunAsync(Portfolio.PortfolioId, sale, Rate, Math.Clamp(TaxRate.Value, 0, 100) / 100, Region.YearStart);
    }
}
