using System.Data;
using System.Data.Common;
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

    /// <summary>
    /// Interest paid from elsewhere (e.g. the bank) that doesn't change what's owed. Only made from the ledger, never entered.
    /// </summary>
    public const string InterestPaid = "InterestPaid";

    /// <summary>+1 when the entry adds to what's owed (interest and fees charged to the loan), nil for interest paid from elsewhere.</summary>
    public static int Sign(string k) => k switch { Repayment => -1, InterestPaid => 0, _ => 1 };

    public static bool IsInterest(string k) => k is Interest or InterestPaid;

    public static string Text(string k) => k switch
    {
        Drawdown => "Drawdown",
        Repayment => "Repayment",
        Interest => "Interest charged",
        Redraw => "Private redraw (not deductible)",
        InterestPaid => "Interest paid",
        _ => "Fee charged",
    };
}

/// <summary>
/// A margin or investment loan (tblLoans). DeductiblePercent is the part used for income-producing investments.
/// AccountId is the liability account its entries post to and its balance is read from; InterestAccountId, when set,
/// is the expense account its interest is read from.
/// </summary>
public sealed record Loan(int Id, string Name, string? Lender, string Kind, decimal RatePercent, decimal DeductiblePercent, decimal? Limit,
    DateOnly Opened, string? Comment, int? AccountId = null, int? InterestAccountId = null);

/// <summary>An entry on a loan. FromLedger marks one read from the ledger (a journal or bank allocation), not entered on the Loans page.</summary>
public sealed record LoanEntry(int Id, int LoanId, DateOnly Date, string Kind, decimal Amount, string? Comment, bool FromLedger = false)
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
        return new LoanYear(loan, opening, Sum(LoanEntryKinds.Drawdown) + Sum(LoanEntryKinds.Redraw), Sum(LoanEntryKinds.Repayment),
            Sum(LoanEntryKinds.Interest) + Sum(LoanEntryKinds.InterestPaid),
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
        var since = mine.Where(e => LoanEntryKinds.IsInterest(e.Kind)).Select(e => (DateOnly?)e.Date).Max() ?? loan.Opened;
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

/// <summary>
/// One ledger journal's effect on a loan: what it added to the loan's liability account (credits less debits) and what
/// it charged to the loan's interest account (debits less credits).
/// </summary>
public sealed record LedgerLoanMove(int LoanId, int JournalId, DateOnly Date, string? Description, decimal LoanCredit, decimal Interest);

/// <summary>A liability or expense account a loan can be read from, with its balance (liabilities: what's owed).</summary>
public sealed record LoanLedgerAccount(int Id, int Number, string Name, string Type, decimal Balance)
{
    public string Display => $"{Number} {Name}";
}

public static class LedgerLoanEntries
{
    /// <summary>
    /// Turns ledger journals into loan entries. Interest charged to the loan in the same journal is interest; the rest of
    /// what the loan went up by is a drawdown, and what it went down by a repayment. Interest paid from elsewhere (the
    /// bank or an offset account) is interest that leaves the balance alone.
    /// </summary>
    public static IReadOnlyList<LoanEntry> From(IEnumerable<LedgerLoanMove> moves)
    {
        var list = new List<LoanEntry>();
        foreach (var m in moves)
        {
            var onLoan = m.LoanCredit > 0 && m.Interest > 0 ? Math.Min(m.LoanCredit, m.Interest) : 0;
            void Add(string kind, decimal amount)
            {
                if (amount != 0)
                    list.Add(new LoanEntry(-m.JournalId, m.LoanId, m.Date, kind, amount, m.Description, FromLedger: true));
            }
            Add(LoanEntryKinds.Interest, onLoan);
            Add(m.LoanCredit > 0 ? LoanEntryKinds.Drawdown : LoanEntryKinds.Repayment, Math.Abs(m.LoanCredit) - onLoan);
            Add(LoanEntryKinds.InterestPaid, m.Interest - onLoan);
        }
        return list;
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
            if (loans.Count == 0)
                return (loans, entries);
            var (linked, moves) = await LedgerAsync(con, portfolioId);
            loans = loans.Select(l => linked.TryGetValue(l.Id, out var a) ? l with { AccountId = a.AccountId, InterestAccountId = a.InterestAccountId } : l).ToList();
            entries.AddRange(LedgerLoanEntries.From(moves));
            return (loans, entries.OrderBy(e => e.Date).ThenBy(e => e.FromLedger).ThenBy(e => e.Id).ToList());
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208)
        {
            return ([], []);
        }
    }

    private sealed record LinkRow(int LoanId, int? AccountId, int? InterestAccountId);
    private sealed record MoveRow(int LoanId, int JournalId, DateTime Date, string? Description, decimal LoanCredit, decimal Interest);

    /// <summary>
    /// Each loan's accounts, and the ledger journals in them other than the loan's own entries (already in tblLoanEntries)
    /// and year-end interest accruals. Nothing until 2026-11-07-loan-ledger-accounts.sql has been run.
    /// </summary>
    private static async Task<(Dictionary<int, LinkRow> Links, IReadOnlyList<LedgerLoanMove> Moves)> LedgerAsync(DbConnection con, int portfolioId)
    {
        try
        {
            using var grid = await con.QueryMultipleAsync("""
                SELECT iLoanID AS LoanId, iAccountID AS AccountId, iInterestAccountID AS InterestAccountId FROM tblLoans WHERE iUserID = @portfolioId;
                SELECT L.iLoanID AS LoanId, J.iJournalID AS JournalId, CAST(J.dtmTransactionDate AS datetime) AS Date, J.strDescription AS Description,
                       CAST(SUM(CASE WHEN JL.iAccountID = L.iAccountID THEN ISNULL(JL.dblCredit, 0) - ISNULL(JL.dblDebit, 0) ELSE 0 END) AS DECIMAL(19,2)) AS LoanCredit,
                       CAST(SUM(CASE WHEN JL.iAccountID = L.iInterestAccountID THEN ISNULL(JL.dblDebit, 0) - ISNULL(JL.dblCredit, 0) ELSE 0 END) AS DECIMAL(19,2)) AS Interest
                FROM tblLoans L
                JOIN tblJournalLines JL ON JL.iAccountID = L.iAccountID OR JL.iAccountID = L.iInterestAccountID
                JOIN tblJournal J ON J.iJournalID = JL.iJournalID AND J.iUserID = L.iUserID
                WHERE L.iUserID = @portfolioId AND ISNULL(J.strSourceModule, '') NOT IN ('LoanEntry', 'InterestAccrual')
                GROUP BY L.iLoanID, J.iJournalID, J.dtmTransactionDate, J.strDescription;
                """, new { portfolioId });
            var links = (await grid.ReadAsync<LinkRow>()).ToDictionary(l => l.LoanId);
            var moves = (await grid.ReadAsync<MoveRow>())
                .Select(m => new LedgerLoanMove(m.LoanId, m.JournalId, DateOnly.FromDateTime(m.Date), m.Description?.Trim(), m.LoanCredit, m.Interest)).ToList();
            return (links, moves);
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 207 or 208)
        {
            return ([], []);
        }
    }

    /// <summary>
    /// The portfolio's own postable liability accounts and the postable expense accounts it can use, with balances, for
    /// picking a loan's accounts.
    /// </summary>
    public async Task<IReadOnlyList<LoanLedgerAccount>> GetLedgerAccountsAsync(int portfolioId)
    {
        await using var con = db.Create();
        return (await con.QueryAsync<LoanLedgerAccount>("""
            SELECT A.iAccountID AS Id, A.iAccountNumber AS Number, A.strAccountName AS Name, A.strAccountType AS Type,
                   CAST((CASE WHEN A.strAccountType = 'Liability' THEN -1 ELSE 1 END)
                        * ISNULL((SELECT SUM(ISNULL(JL.dblDebit, 0) - ISNULL(JL.dblCredit, 0))
                                  FROM tblJournalLines JL JOIN tblJournal J ON J.iJournalID = JL.iJournalID
                                  WHERE JL.iAccountID = A.iAccountID AND J.iUserID = @portfolioId), 0) AS DECIMAL(19,2)) AS Balance
            FROM tblChartOfAccounts A
            WHERE ISNULL(A.bIsPostable, 1) = 1
              AND ((A.strAccountType = 'Liability' AND A.iUserID = @portfolioId) OR (A.strAccountType = 'Expense' AND A.iUserID IN (@portfolioId, 0)))
            ORDER BY A.iAccountNumber, A.strAccountName
            """, new { portfolioId })).ToList();
    }

    /// <summary>
    /// Sets the account the loan's balance is read from and its interest account, then re-posts the loan's own entries so
    /// they move to that account, in one transaction.
    /// </summary>
    public async Task<ProcResult> SetAccountsAsync(int portfolioId, int loanId, int? accountId, int? interestAccountId)
    {
        await using var con = db.Create();
        await con.OpenAsync();
        await using var tx = await con.BeginTransactionAsync();
        var oldAccountId = await con.ExecuteScalarAsync<int?>("SELECT iAccountID FROM tblLoans WITH (UPDLOCK, HOLDLOCK) WHERE iLoanID = @loanId AND iUserID = @portfolioId",
            new { loanId, portfolioId }, tx);
        var result = ProcResult.From(await con.ExecuteScalarAsync<string>("uspSetLoanAccounts",
            new { iUserID = portfolioId, iLoanID = loanId, iAccountID = accountId, iInterestAccountID = interestAccountId }, tx,
            commandType: CommandType.StoredProcedure), "Success");
        if (!result.Ok)
            return result;
        // Only a new liability account moves the entries' journals (an interest account change leaves locked periods alone)
        if (oldAccountId != accountId)
        {
            var entryIds = await con.QueryAsync<int>("SELECT iLoanEntryID FROM tblLoanEntries WHERE iLoanID = @loanId", new { loanId }, tx);
            foreach (var id in entryIds)
                await con.ExecuteAsync(PostJournal, new { iUserID = portfolioId, iLoanEntryID = id }, tx);
        }
        await tx.CommitAsync();
        return result;
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
