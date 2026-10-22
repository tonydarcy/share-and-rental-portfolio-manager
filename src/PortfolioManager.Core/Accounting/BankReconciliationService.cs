using System.Data;
using Dapper;
using PortfolioManager.Core.Data;

namespace PortfolioManager.Core.Accounting;

/// <summary>A bank statement reconciled, or being reconciled, against one ledger account.</summary>
public sealed record BankReconciliation(
    int ReconciliationId,
    int AccountId,
    DateOnly StatementDate,
    decimal OpeningBalance,
    decimal StatementBalance,
    DateTime? Completed,
    decimal TickedTotal = 0,
    int TickedCount = 0)
{
    public bool IsComplete => Completed is not null;

    /// <summary>Nil on a finished reconciliation, unless a ticked line has since been changed or deleted.</summary>
    public decimal Difference => StatementBalance - OpeningBalance - TickedTotal;
}

/// <summary>
/// A ledger line on the bank account. Amount is debit less credit: positive is money in.
/// ReconciliationId is the statement it is ticked on, if any.
/// </summary>
public sealed record BankRecLine(int LineId, DateOnly Date, string Description, string SourceModule, decimal Amount, int? ReconciliationId)
{
    public decimal Deposit => Amount > 0 ? Amount : 0;
    public decimal Withdrawal => Amount < 0 ? -Amount : 0;
}

/// <summary>The screen: the statement, the lines to tick, and the ledger balance at the statement date.</summary>
public sealed record BankRecWorksheet(
    BankReconciliation? Current,
    DateOnly StatementDate,
    decimal OpeningBalance,
    bool OpeningFixed,
    DateOnly? LastFinished,
    IReadOnlyList<BankRecLine> Lines,
    decimal LedgerBalance)
{
    public IReadOnlyList<BankRecLine> Ticked => Current is { } c ? Lines.Where(l => l.ReconciliationId == c.ReconciliationId).ToList() : [];
}

/// <summary>
/// The reconciliation statement accountants keep on file: the statement balance, plus deposits not
/// yet on the statement, less withdrawals not yet presented, should be the ledger balance.
/// </summary>
public sealed record BankRecSummary(decimal StatementBalance, decimal OutstandingDeposits, decimal UnpresentedWithdrawals, decimal LedgerBalance)
{
    public decimal Calculated => StatementBalance + OutstandingDeposits - UnpresentedWithdrawals;
    public decimal Unexplained => LedgerBalance - Calculated;

    public static BankRecSummary From(decimal statementBalance, IEnumerable<BankRecLine> unticked, decimal ledgerBalance)
    {
        var lines = unticked.ToList();
        return new BankRecSummary(statementBalance, lines.Sum(l => l.Deposit), lines.Sum(l => l.Withdrawal), ledgerBalance);
    }
}

public sealed class BankRecInput
{
    public int? ReconciliationId { get; set; }
    public int AccountId { get; set; }
    public DateTime StatementDate { get; set; } = DateTime.Today;
    public decimal OpeningBalance { get; set; }
    public decimal StatementBalance { get; set; }
    public List<int> Ticked { get; set; } = [];

    /// <summary>The ticked ids as the proc's XML list.</summary>
    public string TickedXml => string.Concat(Ticked.Distinct().Select(id => $"<i>{id}</i>"));

    public static decimal Difference(decimal opening, decimal statement, IEnumerable<decimal> tickedAmounts) =>
        statement - opening - tickedAmounts.Sum();
}

/// <summary>Bank reconciliation: tick ledger lines off against a statement until the difference is nil.</summary>
public sealed class BankReconciliationService(IDbConnectionFactory db, ChartOfAccountsService accounts)
{
    /// <summary>The accounts a statement can be reconciled against: the bank import accounts.</summary>
    public Task<IReadOnlyList<AccountChoice>> GetAccountsAsync(int portfolioId) => accounts.GetImportAccountsAsync(portfolioId);

    public async Task<IReadOnlyList<BankReconciliation>> GetHistoryAsync(int portfolioId, int accountId)
    {
        await using var con = db.Create();
        var rows = await con.QueryAsync<RecRow>("""
            SELECT R.iReconciliationID AS ReconciliationId, R.iAccountID AS AccountId, R.dtmStatementDate AS StatementDate,
                   R.dblOpeningBalance AS OpeningBalance, R.dblStatementBalance AS StatementBalance, R.dtmCompletedDate AS Completed,
                   ISNULL(T.Total, 0) AS TickedTotal, ISNULL(T.Lines, 0) AS TickedCount
            FROM tblBankReconciliation R
            OUTER APPLY (
                SELECT SUM(ISNULL(L.dblDebit, 0) - ISNULL(L.dblCredit, 0)) AS Total, COUNT(*) AS Lines
                FROM tblBankReconciliationLine RL
                JOIN tblJournalLines L ON L.iJournalLineID = RL.iJournalLineID
                WHERE RL.iReconciliationID = R.iReconciliationID
            ) T
            WHERE R.iUserID = @portfolioId AND R.iAccountID = @accountId
            ORDER BY R.dtmStatementDate DESC, R.iReconciliationID DESC
            """, new { portfolioId, accountId });
        return rows.Select(r => r.ToRecord()).ToList();
    }

    /// <summary>
    /// The lines to show: for a finished reconciliation, the ones ticked on it; otherwise every line
    /// on the account to the statement date that no finished reconciliation has ticked.
    /// </summary>
    public async Task<BankRecWorksheet> GetWorksheetAsync(int portfolioId, int accountId, int? reconciliationId, DateOnly? statementDate)
    {
        var history = await GetHistoryAsync(portfolioId, accountId);
        var current = reconciliationId is { } id ? history.FirstOrDefault(r => r.ReconciliationId == id) : history.FirstOrDefault(r => !r.IsComplete);
        var last = history.Where(r => r.IsComplete && r.ReconciliationId != current?.ReconciliationId).OrderByDescending(r => r.StatementDate).FirstOrDefault();

        var date = current?.StatementDate ?? statementDate ?? DateOnly.FromDateTime(DateTime.Today);
        var openingFixed = current is { IsComplete: true } || last is not null;
        var opening = current is { IsComplete: true } ? current.OpeningBalance : last?.StatementBalance ?? current?.OpeningBalance ?? 0;

        await using var con = db.Create();
        var rows = await con.QueryAsync<LineRow>("""
            SELECT L.iJournalLineID AS LineId, J.dtmTransactionDate AS Date,
                   COALESCE(NULLIF(LTRIM(RTRIM(L.strDescription)), ''), J.strDescription) AS Description,
                   J.strSourceModule AS SourceModule, ISNULL(L.dblDebit, 0) - ISNULL(L.dblCredit, 0) AS Amount,
                   R.iReconciliationID AS ReconciliationId
            FROM tblJournalLines L
            JOIN tblJournal J ON J.iJournalID = L.iJournalID
            LEFT JOIN tblBankReconciliationLine RL ON RL.iJournalLineID = L.iJournalLineID
            LEFT JOIN tblBankReconciliation R ON R.iReconciliationID = RL.iReconciliationID
            WHERE J.iUserID = @portfolioId AND L.iAccountID = @accountId
              AND (   (@finished = 1 AND R.iReconciliationID = @currentId)
                   OR (@finished = 0 AND J.dtmTransactionDate <= @day AND (R.iReconciliationID IS NULL OR R.dtmCompletedDate IS NULL)))
            ORDER BY J.dtmTransactionDate, L.iJournalLineID
            """, new
            {
                portfolioId,
                accountId,
                currentId = current?.ReconciliationId,
                finished = current is { IsComplete: true } ? 1 : 0,
                day = date.ToDateTime(TimeOnly.MinValue),
            });
        var lines = rows.Select(r => new BankRecLine(r.LineId, DateOnly.FromDateTime(r.Date), r.Description?.Trim() ?? "",
            r.SourceModule?.Trim() ?? "", r.Amount, r.ReconciliationId)).ToList();

        var ledger = await con.ExecuteScalarAsync<decimal?>("""
            SELECT SUM(ISNULL(L.dblDebit, 0) - ISNULL(L.dblCredit, 0))
            FROM tblJournalLines L JOIN tblJournal J ON J.iJournalID = L.iJournalID
            WHERE J.iUserID = @portfolioId AND L.iAccountID = @accountId AND J.dtmTransactionDate <= @day
            """, new { portfolioId, accountId, day = date.ToDateTime(TimeOnly.MinValue) }) ?? 0;

        return new BankRecWorksheet(current, date, opening, openingFixed, last?.StatementDate, lines, ledger);
    }

    /// <summary>Unticked lines to the statement date, for the reconciliation statement of a finished one.</summary>
    public async Task<IReadOnlyList<BankRecLine>> GetUnpresentedAsync(int portfolioId, BankReconciliation rec)
    {
        await using var con = db.Create();
        var rows = await con.QueryAsync<LineRow>("""
            SELECT L.iJournalLineID AS LineId, J.dtmTransactionDate AS Date,
                   COALESCE(NULLIF(LTRIM(RTRIM(L.strDescription)), ''), J.strDescription) AS Description,
                   J.strSourceModule AS SourceModule, ISNULL(L.dblDebit, 0) - ISNULL(L.dblCredit, 0) AS Amount,
                   CAST(NULL AS INT) AS ReconciliationId
            FROM tblJournalLines L
            JOIN tblJournal J ON J.iJournalID = L.iJournalID
            WHERE J.iUserID = @portfolioId AND L.iAccountID = @accountId AND J.dtmTransactionDate <= @day
              AND NOT EXISTS (
                  SELECT 1 FROM tblBankReconciliationLine RL
                  JOIN tblBankReconciliation R ON R.iReconciliationID = RL.iReconciliationID
                  WHERE RL.iJournalLineID = L.iJournalLineID AND R.dtmCompletedDate IS NOT NULL AND R.dtmStatementDate <= @day)
            ORDER BY J.dtmTransactionDate, L.iJournalLineID
            """, new { portfolioId, accountId = rec.AccountId, day = rec.StatementDate.ToDateTime(TimeOnly.MinValue) });
        return rows.Select(r => new BankRecLine(r.LineId, DateOnly.FromDateTime(r.Date), r.Description?.Trim() ?? "",
            r.SourceModule?.Trim() ?? "", r.Amount, null)).ToList();
    }

    public async Task<ProcResult> SaveAsync(int portfolioId, BankRecInput input, bool complete)
    {
        if ((await GetAccountsAsync(portfolioId)).All(a => a.AccountId != input.AccountId))
            return ProcResult.Failed("Choose a bank account from the list.");

        await using var con = db.Create();
        var row = await con.QuerySingleOrDefaultAsync<(string? Result, int? ReconciliationId)>("uspSaveBankReconciliation", new
        {
            iUserId = portfolioId,
            iReconciliationID = input.ReconciliationId,
            iAccountID = input.AccountId,
            dtStatementDate = input.StatementDate.Date,
            dblOpeningBalance = input.OpeningBalance,
            dblStatementBalance = input.StatementBalance,
            xmlLineIDs = input.TickedXml,
            bComplete = complete,
        }, commandType: CommandType.StoredProcedure);
        return ProcResult.From(row.Result, "Success", row.ReconciliationId);
    }

    public async Task<ProcResult> ReopenAsync(int portfolioId, int reconciliationId) =>
        await RunAsync("uspReopenBankReconciliation", portfolioId, reconciliationId);

    public async Task<ProcResult> DeleteAsync(int portfolioId, int reconciliationId) =>
        await RunAsync("uspDeleteBankReconciliation", portfolioId, reconciliationId);

    private async Task<ProcResult> RunAsync(string proc, int portfolioId, int reconciliationId)
    {
        await using var con = db.Create();
        var result = await con.QuerySingleOrDefaultAsync<string?>(proc,
            new { iUserId = portfolioId, iReconciliationID = reconciliationId }, commandType: CommandType.StoredProcedure);
        return ProcResult.From(result, "Success");
    }

    private sealed class RecRow
    {
        public int ReconciliationId { get; set; }
        public int AccountId { get; set; }
        public DateTime StatementDate { get; set; }
        public decimal OpeningBalance { get; set; }
        public decimal StatementBalance { get; set; }
        public DateTime? Completed { get; set; }
        public decimal TickedTotal { get; set; }
        public int TickedCount { get; set; }

        public BankReconciliation ToRecord() => new(ReconciliationId, AccountId, DateOnly.FromDateTime(StatementDate),
            OpeningBalance, StatementBalance, Completed, TickedTotal, TickedCount);
    }

    private sealed class LineRow
    {
        public int LineId { get; set; }
        public DateTime Date { get; set; }
        public string? Description { get; set; }
        public string? SourceModule { get; set; }
        public decimal Amount { get; set; }
        public int? ReconciliationId { get; set; }
    }
}
