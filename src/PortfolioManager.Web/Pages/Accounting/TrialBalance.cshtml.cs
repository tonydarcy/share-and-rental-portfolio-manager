using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Accounting;

public class TrialBalanceModel(CurrentPortfolio current, LedgerReportService reports, ChartOfAccountsService chart) : AsAtReportModel
{
    public PortfolioSummary? Portfolio { get; private set; }
    public string Mode { get; private set; } = "";
    public TrialBalanceReport Report { get; private set; } = LedgerReportService.MapTrialBalance([]);

    public async Task OnGetAsync()
    {
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is null)
            return;

        Mode = await chart.GetAccountingModeAsync(Portfolio.PortfolioId);
        Report = await reports.TrialBalanceAsync(Portfolio.PortfolioId, Date);
    }
}
