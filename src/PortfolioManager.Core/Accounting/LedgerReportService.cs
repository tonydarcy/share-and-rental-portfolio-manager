using System.Data;
using Dapper;
using PortfolioManager.Core.Data;

namespace PortfolioManager.Core.Accounting;

/// <summary>
/// One row of a ledger statement as the procs lay it out. RowType is Header, Item, Total,
/// GrandTotal or Spacer; headers and spacers have no amount.
/// </summary>
public sealed record StatementLine(string Label, decimal? Amount, string RowType, int Indent, string Section = "")
{
    public bool IsHeader => RowType == "Header";
    public bool IsSpacer => RowType == "Spacer";
    public bool IsTotal => RowType == "Total";
    public bool IsGrandTotal => RowType == "GrandTotal";
}

public sealed record ProfitLossReport(IReadOnlyList<StatementLine> Lines, decimal Revenue, decimal Expenses, decimal NetProfit);

public sealed record BalanceSheetReport(IReadOnlyList<StatementLine> Lines, decimal Assets, decimal Liabilities, decimal NetAssets, decimal Equity)
{
    /// <summary>Net assets and equity agree when every journal balances.</summary>
    public decimal Difference => NetAssets - Equity;
}

public sealed record CashFlowReport(IReadOnlyList<StatementLine> Lines, decimal NetProfit, decimal TotalCashMovement, decimal ChangeInBank)
{
    /// <summary>The movement worked out from the other accounts should equal the change in the bank accounts.</summary>
    public decimal Difference => TotalCashMovement - ChangeInBank;
}

public sealed record CashTransaction(DateOnly Date, string Description, string Category, decimal Inflow, decimal Outflow);

public sealed record TrialBalanceLine(string Group, int? AccountNumber, string AccountName, decimal? Debit, decimal? Credit, string RowType)
{
    public bool IsHeader => RowType == "Header";
    public bool IsTotal => RowType == "Total";
    public bool IsGrandTotal => RowType == "GrandTotal";
}

public sealed record TrialBalanceReport(IReadOnlyList<TrialBalanceLine> Lines, decimal TotalDebit, decimal TotalCredit)
{
    public decimal Difference => TotalDebit - TotalCredit;
    public bool Balances => Difference == 0;
}

/// <summary>The double-entry reports from the legacy Reports folder, over the existing report procs.</summary>
public sealed class LedgerReportService(IDbConnectionFactory db, RealisedGainService gains)
{
    public async Task<TrialBalanceReport> TrialBalanceAsync(int portfolioId, DateOnly asAt) =>
        MapTrialBalance(await RowsAsync(portfolioId, "uspGetTrialBalance", new { iUserId = portfolioId, dtEndDate = Day(asAt) }));

    public async Task<ProfitLossReport> ProfitLossAsync(int portfolioId, DateOnly from, DateOnly to) =>
        MapProfitLoss(await RowsAsync(portfolioId, "uspGetProfitLossReport", new { iUserId = portfolioId, dtStartDate = Day(from), dtEndDate = Day(to) }));

    public async Task<BalanceSheetReport> BalanceSheetAsync(int portfolioId, DateOnly asAt) =>
        MapBalanceSheet(await RowsAsync(portfolioId, "uspGetBalanceSheet", new { iUserId = portfolioId, dtAsAtDate = Day(asAt) }));

    /// <summary>The cash movement statement the legacy CashMovement page shows (uspGetDetailedCashFlowReport).</summary>
    public async Task<CashFlowReport> CashFlowAsync(int portfolioId, DateOnly from, DateOnly to) =>
        MapCashFlow(await RowsAsync(portfolioId, "uspGetDetailedCashFlowReport", new { iUserId = portfolioId, dtStartDate = Day(from), dtEndDate = Day(to) }));

    /// <summary>
    /// Each movement in the bank accounts (uspGetCashMovementReport). For a single-entry portfolio the
    /// proc lists the cash transactions instead.
    /// </summary>
    public async Task<IReadOnlyList<CashTransaction>> CashTransactionsAsync(int portfolioId, DateOnly from, DateOnly to) =>
        MapCashTransactions(await RowsAsync(portfolioId, "uspGetCashMovementReport", new { iUserId = portfolioId, dtStartDate = Day(from), dtEndDate = Day(to) }));

    /// <summary>Sale journals are brought up to date first, so the reports include realised gains and losses.</summary>
    private async Task<IReadOnlyList<IDictionary<string, object?>>> RowsAsync(int portfolioId, string proc, object args)
    {
        await gains.SyncAsync(portfolioId);
        await using var con = db.Create();
        var rows = await con.QueryAsync(proc, args, commandType: CommandType.StoredProcedure);
        return rows.Cast<IDictionary<string, object?>>().ToList();
    }

    private static DateTime Day(DateOnly d) => d.ToDateTime(TimeOnly.MinValue);

    /// <summary>
    /// Label / Amount / RowType / IndentLevel rows. A total over no accounts comes back NULL and shows
    /// as zero; headers and spacers keep no amount.
    /// </summary>
    public static IReadOnlyList<StatementLine> MapStatement(IEnumerable<IDictionary<string, object?>> rows, string labelColumn = "Label")
    {
        var lines = new List<StatementLine>();
        foreach (var r in rows)
        {
            var type = Db.Str(r, "RowType");
            if (type.Length == 0)
                type = "Item";
            var amount = Db.Dec(r, "Amount");
            lines.Add(new StatementLine(
                Db.Str(r, labelColumn),
                type is "Header" or "Spacer" ? null : amount ?? 0,
                type,
                Db.Int(r, "IndentLevel") ?? 0,
                Db.Str(r, "AccountType")));
        }
        return lines;
    }

    public static ProfitLossReport MapProfitLoss(IEnumerable<IDictionary<string, object?>> rows)
    {
        var lines = MapStatement(rows);
        return new ProfitLossReport(
            lines,
            AmountOf(lines, "Total", "Total Revenue"),
            AmountOf(lines, "Total", "Total Expenses"),
            lines.LastOrDefault(l => l.IsGrandTotal)?.Amount ?? 0);
    }

    public static BalanceSheetReport MapBalanceSheet(IEnumerable<IDictionary<string, object?>> rows)
    {
        var lines = MapStatement(rows, "AccountName");
        return new BalanceSheetReport(
            lines,
            lines.FirstOrDefault(l => l.IsTotal && l.Section == "Asset")?.Amount ?? 0,
            lines.FirstOrDefault(l => l.IsTotal && l.Section == "Liability")?.Amount ?? 0,
            lines.FirstOrDefault(l => l.IsGrandTotal && l.Section == "NetAssets")?.Amount ?? 0,
            lines.FirstOrDefault(l => l.IsGrandTotal && l.Section == "Equity")?.Amount ?? 0);
    }

    public static CashFlowReport MapCashFlow(IEnumerable<IDictionary<string, object?>> rows)
    {
        var lines = MapStatement(rows);
        return new CashFlowReport(
            lines,
            AmountOf(lines, "GrandTotal", "Net Profit"),
            AmountOf(lines, "GrandTotal", "Total Cash Movement"),
            AmountOf(lines, "Total", "Total Change In Bank Balances"));
    }

    public static IReadOnlyList<CashTransaction> MapCashTransactions(IEnumerable<IDictionary<string, object?>> rows) =>
        rows.Select(r => new CashTransaction(
                Db.Date(r, "Date") ?? default,
                Db.Str(r, "Description"),
                Db.Str(r, "Category"),
                Db.Dec(r, "Inflow") ?? 0,
                Db.Dec(r, "Outflow") ?? 0))
            .ToList();

    /// <summary>
    /// The trial balance proc's rows, in its order. The last GrandTotal row (TRIAL BALANCE TOTAL)
    /// carries the raw debit and credit totals; the NET PROFIT row is only shown, never added.
    /// </summary>
    public static TrialBalanceReport MapTrialBalance(IEnumerable<IDictionary<string, object?>> rows)
    {
        var lines = rows.Select(r =>
            {
                var type = Db.Str(r, "RowType");
                return new TrialBalanceLine(
                    Db.Str(r, "GroupName"),
                    Db.Int(r, "AccountNumber"),
                    Db.Str(r, "AccountName"),
                    type == "Header" ? null : Db.Dec(r, "Debit") ?? 0,
                    type == "Header" ? null : Db.Dec(r, "Credit") ?? 0,
                    type.Length == 0 ? "Item" : type);
            })
            .ToList();
        var footer = lines.LastOrDefault(l => l.Group == "Footer");
        var items = lines.Where(l => l.RowType == "Item").ToList();
        return new TrialBalanceReport(
            lines,
            footer?.Debit ?? items.Sum(l => l.Debit ?? 0),
            footer?.Credit ?? items.Sum(l => l.Credit ?? 0));
    }

    private static decimal AmountOf(IReadOnlyList<StatementLine> lines, string rowType, string label) =>
        lines.FirstOrDefault(l => l.RowType == rowType && string.Equals(l.Label, label, StringComparison.OrdinalIgnoreCase))?.Amount ?? 0;
}
