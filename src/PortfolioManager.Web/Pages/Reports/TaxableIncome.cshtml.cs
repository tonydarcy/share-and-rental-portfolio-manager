using PortfolioManager.Core.Portfolios;
using PortfolioManager.Core.Tax;
using PortfolioManager.Core.TaxWork;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Reports;

public class TaxableIncomeModel(CurrentPortfolio current, TaxableIncomeService income, CgtDiscountService cgt, RentalService rentals) : PeriodReportModel
{
    public PortfolioSummary? Portfolio { get; private set; }
    public TaxableIncomeReport Report { get; private set; } = TaxableIncomeService.Map(null, []);
    public CgtReturnReport? CapitalGains { get; private set; }

    /// <summary>Item 21 rental schedules, for a financial year only (the schedule is yearly).</summary>
    public RentalSchedule Rentals { get; private set; } = RentalSchedule.None;

    public async Task OnGetAsync()
    {
        ResolvePeriod();
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is null)
            return;

        Report = await income.GetAsync(Portfolio.PortfolioId, Period.From, Period.To);
        CapitalGains = await cgt.GetReturnAsync(Portfolio.PortfolioId, Portfolio.EntityType, Period);
        if (Period.FinancialYear is { } year)
            Rentals = await rentals.GetYearAsync(Portfolio.PortfolioId, year);
    }
}
