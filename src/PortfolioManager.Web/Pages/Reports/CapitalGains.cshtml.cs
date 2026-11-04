using PortfolioManager.Core.Portfolios;
using PortfolioManager.Core.Regions;
using PortfolioManager.Core.Tax;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Reports;

public class CapitalGainsModel(CurrentPortfolio current, CgtDiscountService discount, ParcelChoiceService parcelChoices) : PeriodReportModel
{
    public PortfolioSummary? Portfolio { get; private set; }
    public CapitalGainsReport Report { get; private set; } = new([], new(0, 0, 0));
    public CgtReturnReport? Return { get; private set; }

    /// <summary>Sales in the period whose parcels were chosen rather than first in, first out.</summary>
    public int ChosenSales { get; private set; }

    /// <summary>The country's own allowance or inclusion line (UK, Canada, India), if any.</summary>
    public CgtAllowanceLine? Allowance { get; private set; }

    /// <summary>"wash sale" (US) or "superficial loss" (Canada) when the country has a 30-day repurchase rule.</summary>
    public string? RepurchaseRule { get; private set; }

    public IReadOnlyList<RepurchaseWarning> Repurchases { get; private set; } = [];

    public async Task OnGetAsync()
    {
        ResolvePeriod();
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is null)
            return;

        Return = await discount.GetReturnAsync(Portfolio.PortfolioId, Portfolio.EntityType, Period);
        Report = CapitalGainsService.FromEvents(Return.Events);
        var chosen = await parcelChoices.GetAsync(Portfolio.PortfolioId);
        ChosenSales = Return.Events.Where(e => chosen.ContainsKey(e.SellTradeId)).Select(e => e.SellTradeId).Distinct().Count();

        var country = RegionSettings.Current.CountryCode;
        Allowance = CountryCgt.Allowance(country, Portfolio.EntityType, Return.Summary);
        RepurchaseRule = CountryCgt.RepurchaseRuleName(country);
        if (RepurchaseRule is not null)
            Repurchases = CountryCgt.Repurchases(Return.Events, await discount.LoadTradesAsync(Portfolio.PortfolioId));
    }
}
