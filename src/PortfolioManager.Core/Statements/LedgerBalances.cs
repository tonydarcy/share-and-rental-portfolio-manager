using PortfolioManager.Core.Accounting;

namespace PortfolioManager.Core.Statements;

/// <summary>
/// One account's cumulative totals at a date, from the trial balance. Balance is in the account's
/// normal direction: debit less credit for assets and expenses, credit less debit for the rest.
/// </summary>
public sealed record LedgerBalance(int? Number, string Name, string Type, decimal Debit, decimal Credit)
{
    public decimal Balance => Type is "Asset" or "Expense" ? Debit - Credit : Credit - Debit;

    public (int?, string, string) Key => (Number, Name, Type);
}

/// <summary>The trial balance at one date, as account balances.</summary>
public sealed class LedgerSnapshot
{
    private readonly Dictionary<(int?, string, string), LedgerBalance> _byKey;

    public LedgerSnapshot(DateOnly date, IEnumerable<LedgerBalance> accounts, decimal totalDebit, decimal totalCredit)
    {
        Date = date;
        Accounts = accounts.ToList();
        _byKey = Accounts.GroupBy(a => a.Key).ToDictionary(g => g.Key, g => g.Aggregate((a, b) => a with { Debit = a.Debit + b.Debit, Credit = a.Credit + b.Credit }));
        TotalDebit = totalDebit;
        TotalCredit = totalCredit;
    }

    public DateOnly Date { get; }
    public IReadOnlyList<LedgerBalance> Accounts { get; }

    /// <summary>The raw totals of every journal line to the date (the trial balance footer).</summary>
    public decimal TotalDebit { get; }
    public decimal TotalCredit { get; }

    public decimal Balance((int?, string, string) key) => _byKey.TryGetValue(key, out var b) ? b.Balance : 0;
    public decimal Debit((int?, string, string) key) => _byKey.TryGetValue(key, out var b) ? b.Debit : 0;
    public decimal Credit((int?, string, string) key) => _byKey.TryGetValue(key, out var b) ? b.Credit : 0;

    public decimal Total(string type) => Accounts.Where(a => a.Type == type).Sum(a => a.Balance);

    /// <summary>Revenue less expenses since the ledger began (there are no closing journals).</summary>
    public decimal CumulativeProfit => Total("Revenue") - Total("Expense");

    /// <summary>
    /// Lines of accounts whose type isn't one of the five the trial balance groups: the footer
    /// includes them, the groups don't.
    /// </summary>
    public decimal UngroupedDifference =>
        (TotalDebit - TotalCredit) - (Accounts.Sum(a => a.Debit) - Accounts.Sum(a => a.Credit));

    public static LedgerSnapshot FromTrialBalance(DateOnly date, TrialBalanceReport report)
    {
        var accounts = report.Lines
            .Where(l => l.RowType == "Item")
            .Select(l => TypeOf(l.Group) is { } type
                ? new LedgerBalance(l.AccountNumber, l.AccountName.Trim(), type, l.Debit ?? 0, l.Credit ?? 0)
                : null)
            .OfType<LedgerBalance>();
        return new LedgerSnapshot(date, accounts, report.TotalDebit, report.TotalCredit);
    }

    /// <summary>uspGetTrialBalance's GroupName for each account type.</summary>
    public static string? TypeOf(string group) => group switch
    {
        "Income" => "Revenue",
        "Expenses" => "Expense",
        "CurrAsset" or "NonCurrAsset" => "Asset",
        "CurrLiab" or "NonCurrLiab" => "Liability",
        "Equity" => "Equity",
        _ => null,
    };
}
