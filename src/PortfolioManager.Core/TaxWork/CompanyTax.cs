using System.Data;
using Dapper;
using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Data;
using PortfolioManager.Core.Payouts;

namespace PortfolioManager.Core.TaxWork;

public static class FrankingKinds
{
    public const string Opening = "Opening";
    public const string TaxPaid = "TaxPaid";
    public const string DividendPaid = "DividendPaid";
    public const string Refund = "Refund";
    public const string Other = "Other";
    public const string Received = "Received";

    /// <summary>The kinds that can be entered (Received comes from the payouts).</summary>
    public static readonly IReadOnlyList<string> Entered = [Opening, TaxPaid, DividendPaid, Refund, Other];

    public static string Label(string kind) => kind switch
    {
        Opening => "Opening balance",
        TaxPaid => "Income tax or PAYG instalment paid (credit)",
        DividendPaid => "Franked dividend paid (debit)",
        Refund => "Income tax refund received (debit)",
        Received => "Franking credit received",
        _ => "Other",
    };
}

/// <summary>An entry typed into the franking account. Amount is positive for a credit, negative for a debit.</summary>
public sealed record FrankingEntry(int EntryId, DateOnly Date, string Kind, string Description, decimal Amount);

public sealed class FrankingEntryInput
{
    public DateTime Date { get; set; } = DateTime.Today;
    public string Kind { get; set; } = FrankingKinds.TaxPaid;
    public string Description { get; set; } = "";

    /// <summary>The amount for tax paid, refunds, the opening balance and other entries (a debit opening is negative).</summary>
    public decimal Amount { get; set; }

    /// <summary>For a dividend paid: the cash dividend, how much of it is franked, and the company's tax rate.</summary>
    public decimal Dividend { get; set; }
    public decimal FrankedPercent { get; set; } = 100;
    public decimal TaxRate { get; set; } = TaxCalculation.DefaultCompanyRate;

    /// <summary>The signed amount to store, and the description to store with it.</summary>
    public (decimal Amount, string Description) ToEntry() => Kind switch
    {
        FrankingKinds.DividendPaid => (-FrankingLedger.FrankingDebit(Dividend, FrankedPercent, TaxRate),
            Text($"Dividend of {Dividend:#,##0.00}, {FrankedPercent:0.##}% franked at {TaxRate:0.##}%")),
        FrankingKinds.Refund => (-Math.Abs(Amount), Text("Income tax refund")),
        FrankingKinds.TaxPaid => (Math.Abs(Amount), Text("Income tax paid")),
        FrankingKinds.Opening => (Amount, Text("Opening balance")),
        _ => (Amount, Text("Other")),
    };

    private string Text(string fallback) => string.IsNullOrWhiteSpace(Description) ? fallback : Description.Trim();
}

public sealed record FrankingLine(DateOnly Date, string Kind, string Description, decimal Credit, decimal Debit, decimal Balance, int? EntryId);

/// <summary>
/// A company's franking account for one financial year: credits for tax paid and franking credits
/// received, debits for franked dividends paid and refunds. A debit balance at 30 June is a deficit,
/// and franking deficit tax is payable.
/// </summary>
public sealed record FrankingLedger(DateOnly From, DateOnly To, decimal Opening, IReadOnlyList<FrankingLine> Lines)
{
    public decimal Closing => Lines.Count > 0 ? Lines[^1].Balance : Opening;
    public decimal Credits => Lines.Sum(l => l.Credit);
    public decimal Debits => Lines.Sum(l => l.Debit);
    public bool Deficit => Closing < 0;

    /// <summary>The franking debit for a dividend: the franked part times rate / (1 - rate).</summary>
    public static decimal FrankingDebit(decimal dividend, decimal frankedPercent, decimal ratePercent) =>
        ratePercent is <= 0 or >= 100 ? 0
            : TaxReconciliation.Round(dividend * frankedPercent / 100m * ratePercent / (100m - ratePercent));

    public static FrankingLedger Build(IEnumerable<FrankingEntry> entries, IEnumerable<Payout> payouts, DateOnly from, DateOnly to)
    {
        var all = entries.Select(e => (e.Date, e.Kind, e.Description, e.Amount, EntryId: (int?)e.EntryId))
            .Concat(payouts.Where(p => p.FrankingCredit != 0 && p.Date is not null)
                .Select(p => (Date: DateOnly.FromDateTime(p.Date!.Value), Kind: FrankingKinds.Received,
                    Description: $"{p.StockCode}.{p.StockExchangeCode} dividend", Amount: p.FrankingCredit, EntryId: (int?)null)))
            .Where(x => x.Date <= to)
            .OrderBy(x => x.Date).ThenBy(x => x.Kind == FrankingKinds.Opening ? 0 : 1).ThenBy(x => x.EntryId ?? int.MaxValue)
            .ToList();

        var opening = all.Where(x => x.Date < from).Sum(x => x.Amount);
        var balance = opening;
        var lines = new List<FrankingLine>();
        foreach (var x in all.Where(x => x.Date >= from))
        {
            balance += x.Amount;
            lines.Add(new FrankingLine(x.Date, x.Kind, x.Description, Math.Max(x.Amount, 0), Math.Max(-x.Amount, 0), balance, x.EntryId));
        }
        return new FrankingLedger(from, to, opening, lines);
    }
}

/// <summary>Deferred tax on the market value adjustment, from the ledger (1390, 2850, 5950).</summary>
public sealed record DeferredTaxCheck(decimal Adjustment, decimal Liability, decimal YearExpense, decimal RatePercent)
{
    public const int LiabilityAccount = 2850;
    public const int ExpenseAccount = 5950;

    public decimal Expected => TaxReconciliation.Round(Adjustment * RatePercent / 100m);
    public decimal Difference => Expected - Liability;

    public static DeferredTaxCheck From(LedgerYear ledger, decimal ratePercent) => new(
        ledger.Balance(RevaluationService.AdjustmentAccount),
        ledger.Balance(LiabilityAccount),
        ledger.Movement(a => a.Number == ExpenseAccount && a.Type == "Expense"),
        ratePercent);
}

/// <summary>Franking account entries (tblFrankingEntry).</summary>
public sealed class FrankingAccountService(IDbConnectionFactory db, PayoutService payouts)
{
    public async Task<FrankingLedger> GetLedgerAsync(int portfolioId, DateOnly from, DateOnly to)
    {
        await using var con = db.Create();
        var rows = await con.QueryAsync("uspGetFrankingEntries", new { iUserId = portfolioId, dtTo = to.ToDateTime(TimeOnly.MinValue) },
            commandType: CommandType.StoredProcedure);
        var received = await payouts.GetPayoutsAsync(portfolioId, null, to.ToDateTime(TimeOnly.MinValue));
        return FrankingLedger.Build(Map(rows.Cast<IDictionary<string, object?>>()), received.Payouts, from, to);
    }

    public static IReadOnlyList<FrankingEntry> Map(IEnumerable<IDictionary<string, object?>> rows) =>
        rows.Select(r => Db.Date(r, "dtDate") is { } d
                ? new FrankingEntry(Db.Int(r, "iEntryID") ?? 0, d, Db.Str(r, "strKind"), Db.Str(r, "strDescription"), Db.Dec(r, "dblAmount") ?? 0)
                : null)
            .OfType<FrankingEntry>()
            .Where(e => e.EntryId > 0)
            .ToList();

    public async Task<ProcResult> AddAsync(int portfolioId, FrankingEntryInput input)
    {
        var (amount, description) = input.ToEntry();
        await using var con = db.Create();
        var row = await con.QuerySingleOrDefaultAsync<(string? Result, int? EntryId)>("uspAddFrankingEntry", new
        {
            iUserId = portfolioId,
            dtDate = input.Date.Date,
            strKind = FrankingKinds.Entered.Contains(input.Kind) ? input.Kind : FrankingKinds.Other,
            strDescription = description.Length > 200 ? description[..200] : description,
            dblAmount = amount,
        }, commandType: CommandType.StoredProcedure);
        return ProcResult.From(row.Result, "Success", row.EntryId);
    }

    public async Task<ProcResult> DeleteAsync(int portfolioId, int entryId)
    {
        await using var con = db.Create();
        var result = await con.QuerySingleOrDefaultAsync<string?>("uspDeleteFrankingEntry", new { iUserId = portfolioId, iEntryID = entryId },
            commandType: CommandType.StoredProcedure);
        return ProcResult.From(result, "Success");
    }
}

public sealed record Div7aYear(int Year, decimal Opening, decimal RatePercent, bool RateKnown, decimal Interest, decimal MinimumRepayment, decimal Closing);

/// <summary>
/// Division 7A: the minimum yearly repayment on a complying loan from a private company is
/// opening balance x rate / (1 - (1 + rate)^-years left), at the benchmark interest rate for the
/// income year. The schedule assumes each year's repayment is made on 30 June, so interest is the
/// rate on the opening balance; repayments made earlier in the year reduce the interest.
/// </summary>
public static class Div7a
{
    private static readonly SortedDictionary<int, decimal> Rates = new()
    {
        [2017] = 5.40m, [2018] = 5.30m, [2019] = 5.20m, [2020] = 5.37m, [2021] = 4.52m,
        [2022] = 4.52m, [2023] = 4.77m, [2024] = 8.27m, [2025] = 8.77m, [2026] = 8.37m,
    };

    public static decimal? BenchmarkRate(int year) => Rates.TryGetValue(year, out var r) ? r : null;

    public static decimal LatestRate => Rates.Values.Last();

    public static decimal MinimumRepayment(decimal balance, decimal ratePercent, int yearsLeft)
    {
        if (balance <= 0 || yearsLeft <= 0) return 0;
        var r = (double)(ratePercent / 100m);
        if (r == 0) return TaxReconciliation.Round(balance / yearsLeft);
        var factor = r / (1 - Math.Pow(1 + r, -yearsLeft));
        return TaxReconciliation.Round(balance * (decimal)factor);
    }

    /// <summary>
    /// The schedule from the first repayment year (the year after the loan was made) for the term.
    /// A year with no benchmark rate on file uses the override, else the latest rate, and is flagged.
    /// </summary>
    public static IReadOnlyList<Div7aYear> Schedule(decimal openingBalance, int firstYear, int term, decimal? rateOverride = null)
    {
        var years = new List<Div7aYear>();
        var balance = openingBalance;
        for (var i = 0; i < term && balance > 0; i++)
        {
            var year = firstYear + i;
            var known = BenchmarkRate(year);
            var rate = known ?? rateOverride ?? LatestRate;
            var minimum = MinimumRepayment(balance, rate, term - i);
            var interest = TaxReconciliation.Round(balance * rate / 100m);
            var repayment = Math.Min(minimum, balance + interest);
            var closing = balance + interest - repayment;
            years.Add(new Div7aYear(year, balance, rate, known is not null, interest, repayment, closing));
            balance = closing;
        }
        return years;
    }
}
