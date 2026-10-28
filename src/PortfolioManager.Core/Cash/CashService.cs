using System.Data;
using Dapper;
using PortfolioManager.Core.Data;

namespace PortfolioManager.Core.Cash;

public sealed record CashTransaction(
    int CashTransactionId,
    string AccountName,
    DateTime Date,
    string Comment,
    decimal Withdrawal,
    decimal Deposit,
    decimal Balance,
    string Currency,
    int? TradeId);

/// <summary>
/// The cash transactions list from the legacy Default page (tblBankStatement).
/// Entries are added with uspAddCashTransaction and deleted with uspDeleteCashTransaction (2026-10-28-07); an edit is both.
/// </summary>
public sealed class CashService(IDbConnectionFactory db)
{
    public async Task<IReadOnlyList<string>> GetAccountsAsync(int portfolioId)
    {
        await using var con = db.Create();
        var names = await con.QueryAsync<string>(
            "SELECT DISTINCT strBankAccountName FROM tblBankStatement WHERE iUserId = @portfolioId AND strBankAccountName IS NOT NULL ORDER BY strBankAccountName",
            new { portfolioId });
        return names.ToList();
    }

    /// <summary>
    /// Cash transactions by date (newest first unless oldestFirst), optionally for one account.
    /// The balance is a running total per account and currency, worked out the same way
    /// uspAddCashTransaction does, because the stored closing balance isn't refreshed when a trade is deleted.
    /// </summary>
    public async Task<IReadOnlyList<CashTransaction>> GetTransactionsAsync(int portfolioId, string? account, bool oldestFirst)
    {
        var order = oldestFirst
            ? "Date ASC, CashTransactionId ASC"
            : "Date DESC, CashTransactionId DESC";
        await using var con = db.Create();
        var rows = await con.QueryAsync<CashTransaction>($"""
            SELECT * FROM (
                SELECT
                    iBankStatementId AS CashTransactionId,
                    ISNULL(strBankAccountName, '') AS AccountName,
                    CAST(dtmDateOfTransaction AS datetime) AS Date,
                    ISNULL(strComment, '') AS Comment,
                    CAST(ISNULL(dblWithdrawalAmount, 0) AS decimal(19, 2)) AS Withdrawal,
                    CAST(ISNULL(dblDepositAmount, 0) AS decimal(19, 2)) AS Deposit,
                    CAST(SUM(ISNULL(dblDepositAmount, 0) - ISNULL(dblWithdrawalAmount, 0)) OVER (
                        PARTITION BY strBankAccountName, strCurrency
                        ORDER BY dtmDateOfTransaction ASC, iBankStatementId ASC
                        ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS decimal(19, 2)) AS Balance,
                    strCurrency AS Currency,
                    iShareTradeId AS TradeId
                FROM tblBankStatement
                WHERE iUserId = @portfolioId
            ) AS t
            WHERE (@account IS NULL OR AccountName = @account)
            ORDER BY {order}
            """, new { portfolioId, account });
        return rows.ToList();
    }

    /// <summary>The bank accounts this portfolio has entries in, for the account picker.</summary>
    public async Task<IReadOnlyList<BankAccount>> GetBankAccountsAsync(int portfolioId)
    {
        await using var con = db.Create();
        var accounts = await con.QueryAsync<BankAccount>(
            "SELECT strBankAccountName AS Name, strCurrency AS Currency FROM tblBankStatement " +
            "WHERE iUserId = @portfolioId AND strBankAccountName IS NOT NULL AND strBankAccountName <> '' " +
            "GROUP BY strBankAccountName, strCurrency ORDER BY strBankAccountName, strCurrency",
            new { portfolioId });
        return accounts.ToList();
    }

    public async Task<string> GetPortfolioCurrencyAsync(int portfolioId)
    {
        await using var con = db.Create();
        var currency = await con.ExecuteScalarAsync<string?>(
            "SELECT strPortfolioCurrency FROM tblUsers WHERE iUserId = @portfolioId", new { portfolioId });
        return string.IsNullOrWhiteSpace(currency) ? "AUD" : currency.Trim();
    }

    /// <summary>
    /// Adds a manual deposit or withdrawal with uspAddCashTransaction. In Hybrid or DoubleEntry
    /// mode the proc also posts a journal against Owner's Capital (3100). It runs in a transaction
    /// so the bank row and its journal are saved together.
    /// </summary>
    public async Task AddAsync(int portfolioId, NewCashTransaction cash)
    {
        await using var con = db.Create();
        await con.OpenAsync();
        await using var tx = await con.BeginTransactionAsync();
        await con.ExecuteAsync("uspAddCashTransaction", new DynamicParameters(CashEntry.Parameters(portfolioId, cash)), tx,
            commandType: CommandType.StoredProcedure);
        await tx.CommitAsync();
    }

    /// <summary>One cash transaction, only if it belongs to the portfolio.</summary>
    public async Task<CashTransaction?> GetTransactionAsync(int portfolioId, int cashTransactionId)
    {
        await using var con = db.Create();
        return await con.QuerySingleOrDefaultAsync<CashTransaction>("""
            SELECT iBankStatementId AS CashTransactionId, ISNULL(strBankAccountName, '') AS AccountName,
                   CAST(dtmDateOfTransaction AS datetime) AS Date, ISNULL(strComment, '') AS Comment,
                   CAST(ISNULL(dblWithdrawalAmount, 0) AS decimal(19, 2)) AS Withdrawal,
                   CAST(ISNULL(dblDepositAmount, 0) AS decimal(19, 2)) AS Deposit,
                   CAST(ISNULL(dblClosingBalance, 0) AS decimal(19, 2)) AS Balance,
                   strCurrency AS Currency, iShareTradeId AS TradeId
            FROM tblBankStatement WHERE iUserId = @portfolioId AND iBankStatementId = @cashTransactionId
            """, new { portfolioId, cashTransactionId });
    }

    /// <summary>The Add cash form filled in from a stored entry, for an edit to change.</summary>
    public static CashForm ToForm(CashTransaction t) => new()
    {
        Date = DateOnly.FromDateTime(t.Date),
        Direction = t.Deposit > 0 ? nameof(CashDirection.Deposit) : nameof(CashDirection.Withdrawal),
        Amount = t.Deposit > 0 ? t.Deposit : t.Withdrawal,
        BankAccount = t.AccountName is { Length: > 0 } a ? a : null,
        Currency = t.Currency,
        Comment = t.Comment is { Length: > 0 } c ? c : null,
    };

    /// <summary>
    /// Deletes a cash transaction entered by hand, and its journal, with uspDeleteCashTransaction. A trade's settlement
    /// and an entry ticked off on a bank reconciliation are refused.
    /// </summary>
    public async Task<ProcResult> DeleteAsync(int portfolioId, int cashTransactionId)
    {
        await using var con = db.Create();
        return ProcResult.From(await con.ExecuteScalarAsync<string>("uspDeleteCashTransaction",
            new { iUserId = portfolioId, iBankStatementId = cashTransactionId }, commandType: CommandType.StoredProcedure),
            "Cash Transaction Deleted");
    }

    /// <summary>
    /// Changes a cash transaction: in one transaction it is deleted with uspDeleteCashTransaction and recorded again with
    /// uspAddCashTransaction (posting a fresh journal), so it gets a new id, returned in the result.
    /// </summary>
    public async Task<ProcResult> ReplaceAsync(int portfolioId, int cashTransactionId, NewCashTransaction cash)
    {
        await using var con = db.Create();
        await con.OpenAsync();
        await using var tx = await con.BeginTransactionAsync();

        var deleted = ProcResult.From(await con.ExecuteScalarAsync<string>("uspDeleteCashTransaction",
            new { iUserId = portfolioId, iBankStatementId = cashTransactionId }, tx, commandType: CommandType.StoredProcedure),
            "Cash Transaction Deleted");
        if (!deleted.Ok)
            return deleted;

        await con.ExecuteAsync("uspAddCashTransaction", new DynamicParameters(CashEntry.Parameters(portfolioId, cash)), tx,
            commandType: CommandType.StoredProcedure);
        // The proc doesn't return the new id; it is this portfolio's newest entry, still locked by this transaction
        var newId = await con.ExecuteScalarAsync<int>("SELECT MAX(iBankStatementId) FROM tblBankStatement WHERE iUserId = @portfolioId",
            new { portfolioId }, tx);
        await tx.CommitAsync();
        return ProcResult.Success(newId);
    }
}
