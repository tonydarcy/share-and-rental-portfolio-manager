using PortfolioManager.Core.Portfolios;
using PortfolioManager.Core.Regions;
using PortfolioManager.Core.Reports;
using PortfolioManager.Core.Tax;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Reports;

public class IncomeCalendarModel(CurrentPortfolio current, IncomeCalendarService income) : PeriodReportModel
{
    public PortfolioSummary? Portfolio { get; private set; }
    public RegionSettings Region { get; private set; } = RegionSettings.Australia;
    public IReadOnlyList<UpcomingIncome> Upcoming { get; private set; } = [];
    public IncomeByMonth ByMonth { get; private set; } = new([], []);

    public async Task OnGetAsync()
    {
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is null)
            return;
        Region = await current.GetRegionAsync();
        // This financial year unless another is asked for
        if (Fy is null && From is null && To is null)
            Fy = FinancialYear.EndYearFor(Today, Region.YearStart);
        ResolvePeriod();
        Upcoming = await income.GetUpcomingAsync(Portfolio.PortfolioId, Today);
        ByMonth = await income.GetByMonthAsync(Portfolio.PortfolioId, Period);
    }
}
