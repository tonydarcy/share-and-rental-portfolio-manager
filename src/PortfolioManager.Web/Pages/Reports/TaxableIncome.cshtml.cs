using PortfolioManager.Core.Portfolios;
using PortfolioManager.Core.Tax;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Reports;

public class TaxableIncomeModel(CurrentPortfolio current, TaxableIncomeService income, CgtDiscountService cgt) : PeriodReportModel
{
    public PortfolioSummary? Portfolio { get; private set; }
    public TaxableIncomeReport Report { get; private set; } = TaxableIncomeService.Map(null, []);
    public CgtReturnReport? CapitalGains { get; private set; }

    public async Task OnGetAsync()
    {
        ResolvePeriod();
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is null)
            return;

        Report = await income.GetAsync(Portfolio.PortfolioId, Period.From, Period.To);
        CapitalGains = await cgt.GetReturnAsync(Portfolio.PortfolioId, Portfolio.EntityType, Period);
    }
}
