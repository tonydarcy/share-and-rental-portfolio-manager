using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Web.Pages.Reports;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Accounting;

public class ProfitLossModel(CurrentPortfolio current, LedgerReportService reports, ChartOfAccountsService chart) : PeriodReportModel
{
    public PortfolioSummary? Portfolio { get; private set; }
    public string Mode { get; private set; } = "";
    public ProfitLossReport Report { get; private set; } = LedgerReportService.MapProfitLoss([]);

    public async Task OnGetAsync()
    {
        ResolvePeriod();
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is null)
            return;

        Mode = await chart.GetAccountingModeAsync(Portfolio.PortfolioId);
        Report = await reports.ProfitLossAsync(Portfolio.PortfolioId, Period.From, Period.To);
    }
}
