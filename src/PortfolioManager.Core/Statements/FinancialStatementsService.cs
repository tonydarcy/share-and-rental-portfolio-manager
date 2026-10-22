using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Clients;
using PortfolioManager.Core.Tax;

namespace PortfolioManager.Core.Statements;

/// <summary>
/// Financial statements for a portfolio and financial year, from the trial balance
/// (uspGetTrialBalance) at the end of the year and the two years before it. Nothing is written.
/// </summary>
public sealed class FinancialStatementsService(LedgerReportService reports, ClientService clients, ChartOfAccountsService chart)
{
    /// <summary>Null when the portfolio doesn't exist. The caller passes the selected portfolio's id.</summary>
    public async Task<FinancialStatements?> BuildAsync(int portfolioId, int year)
    {
        var client = await clients.GetAsync(portfolioId);
        if (client is null)
            return null;

        var mode = await chart.GetAccountingModeAsync(portfolioId);
        var ends = new[] { FinancialYear.EndDate(year - 2), FinancialYear.EndDate(year - 1), FinancialYear.EndDate(year) };
        var snapshots = new List<LedgerSnapshot>();
        foreach (var end in ends)
            snapshots.Add(LedgerSnapshot.FromTrialBalance(end, await reports.TrialBalanceAsync(portfolioId, end)));

        return FinancialStatementsBuilder.Build(client, year, snapshots[0], snapshots[1], snapshots[2], mode);
    }
}
