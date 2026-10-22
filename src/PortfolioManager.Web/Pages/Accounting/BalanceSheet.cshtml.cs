using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Accounting;

public class BalanceSheetModel(CurrentPortfolio current, LedgerReportService reports, ChartOfAccountsService chart) : AsAtReportModel
{
    public PortfolioSummary? Portfolio { get; private set; }
    public string Mode { get; private set; } = "";
    public BalanceSheetReport Report { get; private set; } = LedgerReportService.MapBalanceSheet([]);

    public async Task OnGetAsync()
    {
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is null)
            return;

        Mode = await chart.GetAccountingModeAsync(Portfolio.PortfolioId);
        Report = await reports.BalanceSheetAsync(Portfolio.PortfolioId, Date);
    }
}
