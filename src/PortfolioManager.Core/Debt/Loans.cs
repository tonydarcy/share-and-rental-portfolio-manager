using System.Data;
using Dapper;
using PortfolioManager.Core.Data;

namespace PortfolioManager.Core.Debt;

public static class LoanKinds
{
    public const string Margin = "Margin", Investment = "Investment";
    public static readonly string[] All = [Margin, Investment];
    public static string Text(string k) => k == Margin ? "Margin loan" : "Investment loan";
}

public static class LoanEntryKinds
{
    public const string Drawdown = "Drawdown", Repayment = "Repayment", Interest = "Interest", Fee = "Fee", Redraw = "Redraw";
    public static readonly string[] All = [Drawdown, Repayment, Interest, Fee, Redraw];

    /// <summary>+1 when the entry adds to what's owed (interest and fees charged to the loan).</summary>
    public static int Sign(string k) => k == Repayment ? -1 : 1;

    public static string Text(string k) => k switch
    {
        Drawdown => "Drawdown",
        Repayment => "Repayment",
        Interest => "Interest charged",
        Redraw => "Private redraw (not deductible)",
        _ => "Fee charged",
    };
}

/// <summary>A margin or investment loan (tblLoans). DeductiblePercent is the part used for income-producing investments.</summary>
public sealed record Loan(int Id, string Name, string? Lender, string Kind, decimal RatePercent, decimal DeductiblePercent, decimal? Limit,
    DateOnly Opened, string? Comment);

public sealed record LoanEntry(int Id, int LoanId, DateOnly Date, string Kind, decimal Amount, string? Comment)
{
    public decimal Signed => LoanEntryKinds.Sign(Kind) * Amount;
}

/// <summary>
/// A loan's figures for a year: interest and fees charged, the deductible part of them, and the balance owed at each end.
/// PrivateRedrawn is what has been redrawn for private use up to the year end.
/// </summary>
public sealed record LoanYear(Loan Loan, decimal OpeningBalance, decimal Drawdowns, decimal Repayments, decimal Interest, decimal Fees, decimal ClosingBalance,
    decimal PrivateRedrawn = 0)
{
    /// <summary>
    /// The deductible part of the balance: the deductible percentage of it, less what was redrawn for private use, so private
    /// redraws make that much of the interest private (a mixed-purpose loan).
    /// </summary>
    public decimal DeductibleShare => PrivateRedrawn <= 0 || ClosingBalance <= 0
        ? Loan.DeductiblePercent / 100m
        : Math.Max(0, ClosingBalance * Loan.DeductiblePercent / 100m - PrivateRedrawn) / ClosingBalance;

    public decimal Deductible => Math.Round((Interest + Fees) * DeductibleShare, 2, MidpointRounding.AwayFromZero);

    /// <summary>Interest a year at the loan's rate on the closing balance, for a check against what was charged.</summary>
    public decimal ExpectedYearInterest => Math.Round(ClosingBalance * Loan.RatePercent / 100m, 2, MidpointRounding.AwayFromZero);
}

public sealed record LoanReport(IReadOnlyList<LoanYear> Loans, decimal MarketValue)
{
    public decimal Owed => Loans.Sum(l => l.ClosingBalance);
    public decimal Interest => Loans.Sum(l => l.Interest + l.Fees);
    public decimal Deductible => Loans.Sum(l => l.Deductible);

    /// <summary>What's owed as a share of the shares' market value; null with nothing held.</summary>
    public decimal? LoanToValue => MarketValue > 0 ? Math.Round(Owed / MarketValue, 4) : null;
}

public static class LoanCalc
{
    public static LoanYear Year(Loan loan, IEnumerable<LoanEntry> entries, DateOnly from, DateOnly to)
    {
        var mine = entries.Where(e => e.LoanId == loan.Id).ToList();
        var opening = mine.Where(e => e.Date < from).Sum(e => e.Signed);
        var year = mine.Where(e => e.Date >= from && e.Date <= to).ToList();
        decimal Sum(string kind) => year.Where(e => e.Kind == kind).Sum(e => e.Amount);
        return new LoanYear(loan, opening, Sum(LoanEntryKinds.Drawdown) + Sum(LoanEntryKinds.Redraw), Sum(LoanEntryKinds.Repayment), Sum(LoanEntryKinds.Interest),
            Sum(LoanEntryKinds.Fee), opening + year.Sum(e => e.Signed), mine.Where(e => e.Kind == LoanEntryKinds.Redraw && e.Date <= to).Sum(e => e.Amount));
    }

    /// <summary>
    /// Interest owed at the date but not yet charged: the balance at the loan's rate for the days since interest was last
    /// charged (or since the loan was opened), actual days over 365.
    /// </summary>
    public static decimal AccruedInterest(Loan loan, IEnumerable<LoanEntry> entries, DateOnly date)
    {
        var mine = entries.Where(e => e.LoanId == loan.Id && e.Date <= date).ToList();
        var balance = mine.Sum(e => e.Signed);
        if (balance <= 0 || loan.RatePercent <= 0)
            return 0;
        var since = mine.Where(e => e.Kind == LoanEntryKinds.Interest).Select(e => (DateOnly?)e.Date).Max() ?? loan.Opened;
        var days = date.DayNumber - since.DayNumber;
        return days <= 0 ? 0 : Math.Round(balance * loan.RatePercent / 100m * days / 365m, 2, MidpointRounding.AwayFromZero);
    }

    public static IReadOnlyList<string> Validate(Loan l)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(l.Name)) errors.Add("Enter a name for the loan.");
        if (!LoanKinds.All.Contains(l.Kind)) errors.Add("Choose the type of loan.");
        if (l.RatePercent is < 0 or > 100) errors.Add("Check the interest rate.");
        if (l.DeductiblePercent is < 0 or > 100) errors.Add("The deductible part is a percentage from 0 to 100.");
        if (l.Limit is < 0) errors.Add("The limit can't be negative.");
        return errors;
    }
}

/// <summary>tblLoans and tblLoanEntries, written through the procs in 2026-10-21-12-debt-fixed-income.sql.</summary>
public sealed class LoanService(IDbConnectionFactory db)
{
    private sealed record LoanRow(int Id, string Name, string? Lender, string Kind, double RatePercent, double DeductiblePercent, double? Limit, DateTime Opened, string? Comment);
    private sealed record EntryRow(int Id, int LoanId, DateTime Date, string Kind, double Amount, string? Comment);

    /// <summary>Empty until the migration has been run.</summary>
    public async Task<(IReadOnlyList<Loan> Loans, IReadOnlyList<LoanEntry> Entries)> GetAsync(int portfolioId)
    {
        await using var con = db.Create();
        try
        {
            using var grid = await con.QueryMultipleAsync("""
                SELECT iLoanID AS Id, strName AS Name, strLender AS Lender, strKind AS Kind, dblRatePercent AS RatePercent,
                       dblDeductiblePercent AS DeductiblePercent, dblLimit AS Limit, CAST(dtmOpened AS datetime) AS Opened, strComment AS Comment
                FROM tblLoans WHERE iUserID = @portfolioId ORDER BY strName;
                SELECT E.iLoanEntryID AS Id, E.iLoanID AS LoanId, CAST(E.dtmDate AS datetime) AS Date, E.strKind AS Kind, E.dblAmount AS Amount, E.strComment AS Comment
                FROM tblLoanEntries E JOIN tblLoans L ON L.iLoanID = E.iLoanID WHERE L.iUserID = @portfolioId ORDER BY E.dtmDate, E.iLoanEntryID;
                """, new { portfolioId });
            var loans = (await grid.ReadAsync<LoanRow>()).Select(r => new Loan(r.Id, r.Name, r.Lender, r.Kind, M(r.RatePercent), M(r.DeductiblePercent),
                r.Limit is { } lim ? M(lim) : null, DateOnly.FromDateTime(r.Opened), r.Comment)).ToList();
            var entries = (await grid.ReadAsync<EntryRow>()).Select(r => new LoanEntry(r.Id, r.LoanId, DateOnly.FromDateTime(r.Date), r.Kind, M(r.Amount), r.Comment)).ToList();
            return (loans, entries);
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208)
        {
            return ([], []);
        }
    }

    public async Task<ProcResult> SaveAsync(int portfolioId, Loan l)
    {
        await using var con = db.Create();
        var row = await con.QuerySingleAsync<(string Result, int? Id)>("uspSaveLoan", new
        {
            iUserID = portfolioId, iLoanID = l.Id, strName = l.Name.Trim(), strLender = Clean(l.Lender), strKind = l.Kind,
            dblRatePercent = (double)l.RatePercent, dblDeductiblePercent = (double)l.DeductiblePercent, dblLimit = (double?)l.Limit,
            dtmOpened = l.Opened.ToDateTime(TimeOnly.MinValue), strComment = Clean(l.Comment),
        }, commandType: CommandType.StoredProcedure);
        return ProcResult.From(row.Result, "Success", row.Id);
    }

    // (Re)posts or removes a loan entry's ledger journal, once 2026-10-26-07-loans-in-ledger.sql has been run
    private const string PostJournal = """
        IF OBJECT_ID('dbo.uspPostLoanEntryJournal', 'P') IS NOT NULL
            EXEC dbo.uspPostLoanEntryJournal @iUserID, @iLoanEntryID;
        """;

    /// <summary>Deletes the loan, its entries and their journals, in one transaction.</summary>
    public async Task<ProcResult> DeleteAsync(int portfolioId, int loanId)
    {
        await using var con = db.Create();
        await con.OpenAsync();
        await using var tx = await con.BeginTransactionAsync();
        var entryIds = (await con.QueryAsync<int>(
            "SELECT E.iLoanEntryID FROM tblLoanEntries E JOIN tblLoans L ON L.iLoanID = E.iLoanID WHERE L.iLoanID = @loanId AND L.iUserID = @portfolioId",
            new { loanId, portfolioId }, tx)).ToList();
        var result = ProcResult.From(await con.ExecuteScalarAsync<string>("uspDeleteLoan", new { iUserID = portfolioId, iLoanID = loanId }, tx,
            commandType: CommandType.StoredProcedure), "Success");
        if (!result.Ok)
            return result;
        foreach (var id in entryIds)
            await con.ExecuteAsync(PostJournal, new { iUserID = portfolioId, iLoanEntryID = id }, tx);
        await tx.CommitAsync();
        return result;
    }

    /// <summary>Adds the entry and posts its journal (Hybrid and double-entry portfolios), in one transaction.</summary>
    public async Task<ProcResult> AddEntryAsync(int portfolioId, LoanEntry e)
    {
        await using var con = db.Create();
        await con.OpenAsync();
        await using var tx = await con.BeginTransactionAsync();
        var row = await con.QuerySingleAsync<(string Result, int? Id)>("uspAddLoanEntry", new
        {
            iUserID = portfolioId, iLoanID = e.LoanId, dtmDate = e.Date.ToDateTime(TimeOnly.MinValue), strKind = e.Kind,
            dblAmount = (double)e.Amount, strComment = Clean(e.Comment),
        }, tx, commandType: CommandType.StoredProcedure);
        var result = ProcResult.From(row.Result, "Success", row.Id);
        if (!result.Ok || row.Id is not { } id)
            return result;
        await con.ExecuteAsync(PostJournal, new { iUserID = portfolioId, iLoanEntryID = id }, tx);
        await tx.CommitAsync();
        return result;
    }

    /// <summary>Deletes the entry and its journal, in one transaction.</summary>
    public async Task<ProcResult> DeleteEntryAsync(int portfolioId, int entryId)
    {
        await using var con = db.Create();
        await con.OpenAsync();
        await using var tx = await con.BeginTransactionAsync();
        var result = ProcResult.From(await con.ExecuteScalarAsync<string>("uspDeleteLoanEntry", new { iUserID = portfolioId, iLoanEntryID = entryId }, tx,
            commandType: CommandType.StoredProcedure), "Success");
        if (!result.Ok)
            return result;
        await con.ExecuteAsync(PostJournal, new { iUserID = portfolioId, iLoanEntryID = entryId }, tx);
        await tx.CommitAsync();
        return result;
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static decimal M(double d) => Math.Round((decimal)d, 2, MidpointRounding.AwayFromZero);
}
