using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Core.Regions;
using PortfolioManager.Core.Tax;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Reports;

/// <summary>Holdings worth less than they cost that could be sold to offset this year's capital gains. Nothing is saved.</summary>
public class TaxLossModel(CurrentPortfolio current, TaxLossService taxLoss) : PageModel
{
    /// <summary>Percent, e.g. 32.</summary>
    [BindProperty(SupportsGet = true)] public decimal? TaxRate { get; set; }

    public PortfolioSummary? Portfolio { get; private set; }
    public RegionSettings Region { get; private set; } = RegionSettings.Australia;
    public CgtDiscountRate Rate { get; private set; } = CgtDiscountRate.None;
    public TaxLossReport? Report { get; private set; }
    public DateOnly Today => PeriodReportModel.Today;

    public async Task OnGetAsync()
    {
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is null)
            return;
        Region = await current.GetRegionAsync();
        Rate = CgtDiscount.RateFor(Portfolio.EntityType, Region);
        TaxRate ??= WhatIf.DefaultTaxRate(Rate) * 100;
        Report = await taxLoss.GetAsync(Portfolio.PortfolioId, Rate, Math.Clamp(TaxRate.Value, 0, 100) / 100, Today, Region.YearStart);
    }
}
