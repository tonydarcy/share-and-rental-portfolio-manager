using PortfolioManager.Core.Portfolios;
using PortfolioManager.Core.Tax;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Reports;

public class CapitalGainsModel(CurrentPortfolio current, CapitalGainsService gains, CgtDiscountService discount, ParcelChoiceService parcelChoices) : PeriodReportModel
{
    public PortfolioSummary? Portfolio { get; private set; }
    public CapitalGainsReport Report { get; private set; } = new([], new(0, 0, 0));
    public CgtReturnReport? Return { get; private set; }

    /// <summary>Sales in the period whose parcels were chosen rather than first in, first out.</summary>
    public int ChosenSales { get; private set; }

    public async Task OnGetAsync()
    {
        ResolvePeriod();
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is null)
            return;

        Report = await gains.GetAsync(Portfolio.PortfolioId, Period.From, Period.To);
        Return = await discount.GetReturnAsync(Portfolio.PortfolioId, Portfolio.EntityType, Period);
        var chosen = await parcelChoices.GetAsync(Portfolio.PortfolioId);
        ChosenSales = Return.Events.Where(e => chosen.ContainsKey(e.SellTradeId)).Select(e => e.SellTradeId).Distinct().Count();
    }
}
