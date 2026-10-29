using System.Data;
using System.Data.Common;
using Dapper;
using PortfolioManager.Core.Data;
using PortfolioManager.Core.SalesTax;
using PortfolioManager.Core.Payouts;

namespace PortfolioManager.Core.Accounting;

/// <summary>One bank statement upload (uspGetImportHistory).</summary>
public sealed record ImportBatch(string BatchId, DateTime? ImportedOn, int Transactions, int Allocated, decimal TotalValue)
{
    public bool Locked => Allocated > 0;
    public bool FullyAllocated => Transactions > 0 && Allocated >= Transactions;
    public int Percent => Transactions == 0 ? 0 : (int)Math.Round(100m * Allocated / Transactions);
}

/// <summary>An imported line still sitting in Bank Import Suspense. Amount is positive for a deposit.</summary>
public sealed record UnallocatedLine(int LineId, DateOnly Date, string Description, decimal Amount)
{
    public bool IsDeposit => Amount > 0;
}

/// <summary>A payout or trade an imported line could be matched to, and the account the match posts to.</summary>
public sealed record MatchCandidate(int Id, DateOnly Date, string Description, decimal Amount, int TargetAccountId);

public sealed record ImportResult(int Imported, string BatchId);

/// <summary>The outcome of an allocation: null when it worked, otherwise why not.</summary>
public sealed record AllocationResult(string? Error)
{
    public bool Ok => Error is null;
    public static readonly AllocationResult Success = new((string?)null);
}

/// <summary>
/// Bank statement import, import history and allocation of imported lines
/// (ImportBankStatement.aspx, ImportHistory.aspx, Unallocated.aspx).
/// </summary>
public sealed class BankImportService(IDbConnectionFactory db, ChartOfAccountsService accounts, SalesTaxService salesTax)
{
    public const string SuspenseAccountName = "Bank Import Suspense";

    /// <summary>
    /// Transactions already imported into this bank account between the dates, signed as deposits
    /// (+) and withdrawals (-), for the duplicate check.
    /// </summary>
    public async Task<IReadOnlyList<(DateOnly Date, decimal Amount, string Description)>> GetImportedAsync(
        int portfolioId, int bankAccountId, DateOnly from, DateOnly to)
    {
        await using var con = db.Create();
        var rows = await con.QueryAsync<ImportedRow>("""
            SELECT J.dtmTransactionDate AS Date, ISNULL(JL.dblDebit, 0) - ISNULL(JL.dblCredit, 0) AS Amount, J.strDescription AS Description
            FROM tblJournal J
            JOIN tblJournalLines JL ON JL.iJournalID = J.iJournalID
            WHERE J.iUserID = @portfolioId AND J.strSourceModule = 'BankImport'
              AND JL.iAccountID = @bankAccountId
              AND J.dtmTransactionDate BETWEEN @from AND @to
            """, new { portfolioId, bankAccountId, from = from.ToDateTime(TimeOnly.MinValue), to = to.ToDateTime(TimeOnly.MinValue) });
        return rows.Select(r => (DateOnly.FromDateTime(r.Date), r.Amount, r.Description ?? "")).ToList();
    }

    private sealed class ImportedRow
    {
        public DateTime Date { get; set; }
        public decimal Amount { get; set; }
        public string? Description { get; set; }
    }

    /// <summary>
    /// Posts every transaction with uspAddBankImportJournal under one new batch id, in one
    /// transaction: either the whole statement is imported or none of it.
    /// </summary>
    public async Task<ImportResult> ImportAsync(int portfolioId, int bankAccountId, IReadOnlyList<BankTransaction> transactions)
    {
        if ((await accounts.GetImportAccountsAsync(portfolioId)).All(a => a.AccountId != bankAccountId))
            throw new InvalidOperationException("The bank account isn't one this portfolio can import into.");
        if (transactions.Count == 0)
            return new ImportResult(0, "");

        var batchId = Guid.NewGuid().ToString();
        await using var con = db.Create();
        await con.OpenAsync();
        await using var tx = await con.BeginTransactionAsync();
        foreach (var t in transactions)
        {
            await con.ExecuteAsync("uspAddBankImportJournal", new
            {
                iUserId = portfolioId,
                iBankAccountID = bankAccountId,
                dtDate = t.Date.ToDateTime(TimeOnly.MinValue),
                strDescription = t.Description.Length > BankStatementParser.MaxDescription ? t.Description[..BankStatementParser.MaxDescription] : t.Description,
                dblAmount = t.Amount,
                strBatchID = batchId,
            }, tx, commandType: CommandType.StoredProcedure);
        }
        await tx.CommitAsync();
        return new ImportResult(transactions.Count, batchId);
    }

    public async Task<IReadOnlyList<ImportBatch>> GetHistoryAsync(int portfolioId)
    {
        await using var con = db.Create();
        var rows = await con.QueryAsync("uspGetImportHistory", new { iUserId = portfolioId },
            commandType: CommandType.StoredProcedure);
        return MapHistory(rows.Cast<IDictionary<string, object?>>());
    }

    /// <summary>
    /// The proc counts journal lines, two per imported transaction (bank and suspense), so
    /// TransactionCount is halved. Only the suspense line is ever allocated, so AllocatedCount
    /// already counts transactions.
    /// </summary>
    public static IReadOnlyList<ImportBatch> MapHistory(IEnumerable<IDictionary<string, object?>> rows) =>
        rows.Select(r => new ImportBatch(
                Db.Str(r, "strBatchID"),
                Db.Timestamp(r, "ImportDate"),
                (Db.Int(r, "TransactionCount") ?? 0) / 2,
                Db.Int(r, "AllocatedCount") ?? 0,
                Db.Dec(r, "TotalValue") ?? 0))
            .Where(b => b.BatchId.Length > 0)
            .ToList();

    /// <summary>
    /// Deletes a whole upload with uspDeleteImportBatch, which refuses once any of it is allocated.
    /// The batch must be one of this portfolio's.
    /// </summary>
    public async Task<AllocationResult> DeleteBatchAsync(int portfolioId, string batchId)
    {
        if ((await GetHistoryAsync(portfolioId)).All(b => b.BatchId != batchId))
            return new AllocationResult("That import wasn't found in this portfolio.");

        await using var con = db.Create();
        var result = await con.QuerySingleOrDefaultAsync<string?>("uspDeleteImportBatch",
            new { iUserId = portfolioId, strBatchID = batchId }, commandType: CommandType.StoredProcedure);
        return Result(result);
    }

    public async Task<IReadOnlyList<UnallocatedLine>> GetUnallocatedAsync(int portfolioId)
    {
        await using var con = db.Create();
        var rows = await con.QueryAsync("uspGetUnallocatedTransactions", new { iUserId = portfolioId },
            commandType: CommandType.StoredProcedure);
        return MapUnallocated(rows.Cast<IDictionary<string, object?>>());
    }

    /// <summary>The proc's Amount is credit less debit on the suspense line: positive for a deposit.</summary>
    public static IReadOnlyList<UnallocatedLine> MapUnallocated(IEnumerable<IDictionary<string, object?>> rows) =>
        rows.Where(r => Db.Value(r, "iMatchedJournalID") is null)
            .Select(r => new UnallocatedLine(
                Db.Int(r, "iJournalLineID") ?? 0,
                Db.Date(r, "dtmTransactionDate") ?? default,
                Db.Str(r, "strDescription"),
                Db.Dec(r, "Amount") ?? 0))
            .Where(l => l.LineId > 0)
            .ToList();

    /// <summary>One unallocated suspense line, only if it belongs to the portfolio.</summary>
    public async Task<UnallocatedLine?> GetLineAsync(int portfolioId, int lineId)
    {
        await using var con = db.Create();
        return (await LineAsync(con, null, portfolioId, lineId))?.Line;
    }

    private sealed record SuspenseLine(UnallocatedLine Line, int SuspenseAccountId);

    private static async Task<SuspenseLine?> LineAsync(DbConnection con, DbTransaction? tx, int portfolioId, int lineId)
    {
        var row = await con.QuerySingleOrDefaultAsync<LineRow>("""
            SELECT JL.iJournalLineID AS LineId, J.dtmTransactionDate AS Date, J.strDescription AS Description,
                   ISNULL(JL.dblCredit, 0) - ISNULL(JL.dblDebit, 0) AS Amount, JL.iAccountID AS AccountId
            FROM tblJournalLines JL
            JOIN tblJournal J ON J.iJournalID = JL.iJournalID
            JOIN tblChartOfAccounts CA ON CA.iAccountID = JL.iAccountID
            WHERE JL.iJournalLineID = @lineId AND J.iUserID = @portfolioId
              AND JL.iMatchedJournalID IS NULL
              AND J.strSourceModule = 'BankImport'
              AND CA.strAccountName = @suspense AND (CA.iUserID = @portfolioId OR CA.iUserID = 0)
            """, new { portfolioId, lineId, suspense = SuspenseAccountName }, tx);
        return row is { } r
            ? new SuspenseLine(new UnallocatedLine(r.LineId, DateOnly.FromDateTime(r.Date), r.Description?.Trim() ?? "", r.Amount), r.AccountId)
            : null;
    }

    private sealed class LineRow
    {
        public int LineId { get; set; }
        public DateTime Date { get; set; }
        public string? Description { get; set; }
        public decimal Amount { get; set; }
        public int AccountId { get; set; }
    }

    /// <summary>
    /// Assigns the line to an account with uspAllocateSuspenseTransaction, as the legacy Assign button
    /// did. The account must be one of the portfolio's postable accounts. With a tax code, the
    /// account gets the amount less tax and the tax goes to the tax control account; the tax is
    /// worked out from the code's rate unless <paramref name="tax"/> is given.
    /// </summary>
    public async Task<AllocationResult> AllocateToAccountAsync(int portfolioId, int lineId, int accountId, string? description,
        int? taxCodeId = null, decimal? tax = null)
    {
        if ((await accounts.GetJournalAccountsAsync(portfolioId)).All(a => a.AccountId != accountId))
            return new AllocationResult("Choose an account from the list.");

        TaxCode? code = null;
        if (taxCodeId is { } codeId)
        {
            code = (await salesTax.GetActiveCodesAsync(portfolioId)).FirstOrDefault(c => c.TaxCodeId == codeId);
            if (code is null)
                return new AllocationResult("Choose a tax code from the list.");
        }

        await using var con = db.Create();
        await con.OpenAsync();
        if (await LineAsync(con, null, portfolioId, lineId) is not { } line)
            return new AllocationResult("That transaction wasn't found, or it has already been allocated.");

        decimal? taxAmount = null;
        if (code is not null)
        {
            var gross = Math.Abs(line.Line.Amount);
            taxAmount = tax ?? TaxMath.TaxInGross(gross, code.Rate);
            if (taxAmount < 0 || taxAmount > gross || decimal.Round(taxAmount.Value, 2) != taxAmount)
                return new AllocationResult($"The tax must be in cents, from 0 up to {gross:C}.");
        }

        var text = string.IsNullOrWhiteSpace(description) ? line.Line.Description : description.Trim();
        return await AllocateAsync(con, null, portfolioId, lineId, accountId, text, code?.TaxCodeId, taxAmount);
    }

    /// <summary>Payouts whose cash matches a deposit and whose journal hasn't been matched to an import already.</summary>
    public async Task<IReadOnlyList<MatchCandidate>> GetPayoutMatchesAsync(int portfolioId, int lineId)
    {
        await using var con = db.Create();
        await con.OpenAsync();
        if (await LineAsync(con, null, portfolioId, lineId) is not { } line || !line.Line.IsDeposit)
            return [];
        return await PayoutMatchesAsync(con, null, portfolioId, line, null);
    }

    /// <summary>Trades whose settlement matches the line (a buy for a withdrawal, a sell for a deposit).</summary>
    public async Task<IReadOnlyList<MatchCandidate>> GetTradeMatchesAsync(int portfolioId, int lineId)
    {
        await using var con = db.Create();
        await con.OpenAsync();
        if (await LineAsync(con, null, portfolioId, lineId) is not { } line)
            return [];
        return await TradeMatchesAsync(con, null, portfolioId, line, null);
    }

    /// <summary>
    /// Matches a deposit to a payout already recorded. The payout's journal put the cash in a bank
    /// account; the allocation moves the import out of suspense against that same account, so the
    /// payout's income stays and the cash is counted once, in the imported bank account.
    /// </summary>
    public async Task<AllocationResult> MatchPayoutAsync(int portfolioId, int lineId, int payoutId)
    {
        await using var con = db.Create();
        await con.OpenAsync();
        if (await LineAsync(con, null, portfolioId, lineId) is not { } line)
            return new AllocationResult("That transaction wasn't found, or it has already been allocated.");
        var match = (await PayoutMatchesAsync(con, null, portfolioId, line, payoutId)).FirstOrDefault();
        if (match is null)
            return new AllocationResult("That payout can't be matched to this transaction (it isn't in this portfolio, the amount differs, it has no ledger journal, or it is already matched).");
        return await AllocateAsync(con, null, portfolioId, lineId, match.TargetAccountId, MatchDescription("payout", payoutId, match.Description));
    }

    /// <summary>
    /// Matches the line to a trade. A trade with its own cash transaction already took the cash
    /// from a bank account, so the allocation reverses that against the imported bank account;
    /// a trade without one is still owed in Trade Clearing, so the allocation settles it.
    /// </summary>
    public async Task<AllocationResult> MatchTradeAsync(int portfolioId, int lineId, int tradeId)
    {
        await using var con = db.Create();
        await con.OpenAsync();
        if (await LineAsync(con, null, portfolioId, lineId) is not { } line)
            return new AllocationResult("That transaction wasn't found, or it has already been allocated.");
        var match = (await TradeMatchesAsync(con, null, portfolioId, line, tradeId)).FirstOrDefault();
        if (match is null)
            return new AllocationResult("That trade can't be matched to this transaction (it isn't in this portfolio, the amount or direction differs, it has no ledger journal, or it is already matched).");
        return await AllocateAsync(con, null, portfolioId, lineId, match.TargetAccountId, MatchDescription("trade", tradeId, match.Description));
    }

    /// <summary>
    /// The legacy "Payout" button: records a new payout for a deposit with uspAddPayout and matches
    /// the line to it, in one transaction. The cash received is the deposit's amount.
    /// </summary>
    public async Task<AllocationResult> AddPayoutAndMatchAsync(int portfolioId, int lineId, PayoutInput input)
    {
        if (!AccountingMode.PostsJournals(await accounts.GetAccountingModeAsync(portfolioId)))
            return new AllocationResult("This portfolio uses single-entry accounting, so payouts don't post ledger journals to match. Assign the deposit to an income account instead.");

        await using var con = db.Create();
        await con.OpenAsync();
        await using var tx = await con.BeginTransactionAsync();

        if (await LineAsync(con, tx, portfolioId, lineId) is not { } line)
            return new AllocationResult("That transaction wasn't found, or it has already been allocated.");
        if (!line.Line.IsDeposit)
            return new AllocationResult("Only a deposit can be recorded as a payout.");
        if (input.Total != line.Line.Amount)
            return new AllocationResult($"The cash received must be the deposit's amount, {line.Line.Amount:C}.");

        await con.ExecuteAsync("uspAddPayout", PayoutService.AddParameters(portfolioId, input), tx, commandType: CommandType.StoredProcedure);

        // The payout just added (uspAddPayout doesn't return its id)
        var payoutId = await con.QuerySingleOrDefaultAsync<int?>("""
            SELECT TOP 1 iPayoutID FROM tblPayouts
            WHERE iUserID = @portfolioId AND strStockCode = @code AND dtmTransactionDate = @date
            ORDER BY iPayoutID DESC
            """, new { portfolioId, code = input.StockCode.Trim().ToUpperInvariant(), date = input.Date.Date }, tx);
        var match = payoutId is { } id ? (await PayoutMatchesAsync(con, tx, portfolioId, line, id)).FirstOrDefault() : null;
        if (match is null)
            throw new InvalidOperationException("The new payout's journal wasn't found, so it couldn't be matched; nothing was saved.");

        var result = await AllocateAsync(con, tx, portfolioId, lineId, match.TargetAccountId, MatchDescription("payout", match.Id, match.Description));
        if (!result.Ok)
            throw new InvalidOperationException($"The new payout couldn't be matched ({result.Error}); nothing was saved.");

        await tx.CommitAsync();
        return result;
    }

    /// <summary>
    /// Records a bank line as a rental entry for a property (rent in, or property expenses out) and matches the line to it,
    /// in one transaction, so the amounts reach that property's rental schedule and the cash is counted once, in the
    /// imported bank account. The entry's net (income less expenses) must be the line's amount.
    /// </summary>
    public async Task<AllocationResult> AddRentalAndMatchAsync(int portfolioId, int lineId, RentalPayoutInput input)
    {
        if (!AccountingMode.PostsJournals(await accounts.GetAccountingModeAsync(portfolioId)))
            return new AllocationResult("This portfolio uses single-entry accounting, so rental entries don't post ledger journals to match. Allocate the line to an account instead.");

        await using var con = db.Create();
        await con.OpenAsync();
        await using var tx = await con.BeginTransactionAsync();

        if (await LineAsync(con, tx, portfolioId, lineId) is not { } line)
            return new AllocationResult("That transaction wasn't found, or it has already been allocated.");
        if (RentalMismatch(line.Line.Amount, input.Net) is { } mismatch)
            return new AllocationResult(mismatch);
        input.Date = line.Line.Date.ToDateTime(TimeOnly.MinValue);

        var payoutId = await PayoutService.AddRentalAsync(con, tx, portfolioId, input);
        var match = (await PayoutMatchesAsync(con, tx, portfolioId, line, payoutId)).FirstOrDefault()
            ?? throw new InvalidOperationException("The new rental entry's journal wasn't found, so it couldn't be matched; nothing was saved.");

        var result = await AllocateAsync(con, tx, portfolioId, lineId, match.TargetAccountId, MatchDescription("payout", match.Id, match.Description));
        if (!result.Ok)
            throw new InvalidOperationException($"The new rental entry couldn't be matched ({result.Error}); nothing was saved.");

        await tx.CommitAsync();
        return result;
    }

    /// <summary>Why a rental entry's net can't settle a bank line, or null when it can (a deposit is income less expenses; a withdrawal the reverse).</summary>
    public static string? RentalMismatch(decimal lineAmount, decimal net) =>
        Math.Round(net, 2) == Math.Round(lineAmount, 2)
            ? null
            : lineAmount >= 0
                ? $"The rent less expenses comes to {net:N2}, but the deposit is {lineAmount:N2}. They must be the same."
                : $"The expenses less any rent come to {-net:N2}, but the withdrawal is {-lineAmount:N2}. They must be the same.";

    private static async Task<AllocationResult> AllocateAsync(DbConnection con, DbTransaction? tx, int portfolioId, int lineId, int accountId, string description,
        int? taxCodeId = null, decimal? taxAmount = null)
    {
        // The tax parameters are only sent when there is a tax code, so this works before the sales tax migration
        object parameters = taxCodeId is null
            ? new
            {
                iUserId = portfolioId,
                iSuspenseLineID = lineId,
                iTargetAccountID = accountId,
                strDescription = description.Length > 500 ? description[..500] : description,
            }
            : new
            {
                iUserId = portfolioId,
                iSuspenseLineID = lineId,
                iTargetAccountID = accountId,
                strDescription = description.Length > 500 ? description[..500] : description,
                iTaxCodeID = taxCodeId,
                dblTaxAmount = taxAmount ?? 0,
            };
        var result = await con.QuerySingleOrDefaultAsync<string?>("uspAllocateSuspenseTransaction", parameters, tx, commandType: CommandType.StoredProcedure);
        return Result(result);
    }

    /// <summary>
    /// The allocation journal's description names what it was matched to, so the same payout or
    /// trade isn't offered again.
    /// </summary>
    public static string MatchDescription(string kind, int id, string what) => $"Matched to {kind} {id}: {what}";

    private static async Task<IReadOnlyList<MatchCandidate>> PayoutMatchesAsync(DbConnection con, DbTransaction? tx, int portfolioId, SuspenseLine line, int? payoutId)
    {
        // The cash line of the payout's journal: marked by description since the ledger-fixes
        // migration, otherwise the first balance sheet line that isn't a tax or franking credit
        var rows = await con.QueryAsync<CandidateRow>("""
            SELECT TOP 20 P.iPayoutID AS Id, P.dtmTransactionDate AS Date, P.strStockExchangeCode AS Exchange, P.strStockCode AS Code,
                   P.strComment AS Comment, CAST(ROUND(ISNULL(P.dblTotal, 0), 2) AS DECIMAL(19,2)) AS Amount, Cash.iAccountID AS TargetAccountId
            FROM tblPayouts P
            CROSS APPLY (
                SELECT TOP 1 JL.iAccountID
                FROM tblJournal J
                JOIN tblJournalLines JL ON JL.iJournalID = J.iJournalID
                JOIN tblChartOfAccounts CA ON CA.iAccountID = JL.iAccountID
                WHERE J.iUserID = @portfolioId AND J.strSourceModule = 'Payout' AND J.iSourceID = P.iPayoutID
                  AND (JL.strDescription IN ('Cash received', 'Cash paid')
                       OR (CA.strAccountType NOT IN ('Revenue', 'Expense') AND CA.iAccountNumber NOT IN (1400, 1410, 1420)))
                ORDER BY CASE WHEN JL.strDescription IN ('Cash received', 'Cash paid') THEN 0 ELSE 1 END, J.iJournalID, JL.iJournalLineID
            ) Cash
            WHERE P.iUserID = @portfolioId
              AND (@payoutId IS NULL OR P.iPayoutID = @payoutId)
              AND ABS(ROUND(ISNULL(P.dblTotal, 0), 2) - @amount) < 0.005
              AND Cash.iAccountID <> @suspenseAccountId
              AND NOT EXISTS (
                  SELECT 1 FROM tblJournal A
                  WHERE A.iUserID = @portfolioId AND A.strSourceModule = 'ManualAllocation'
                    AND A.strDescription LIKE 'Matched to payout ' + CAST(P.iPayoutID AS VARCHAR(12)) + ':%')
            ORDER BY ABS(DATEDIFF(DAY, P.dtmTransactionDate, @date)), P.iPayoutID DESC
            """, new
            {
                portfolioId,
                payoutId,
                amount = line.Line.Amount,
                suspenseAccountId = line.SuspenseAccountId,
                date = line.Line.Date.ToDateTime(TimeOnly.MinValue),
            }, tx);

        return rows.Select(r => new MatchCandidate(
                r.Id,
                r.Date is { } d ? DateOnly.FromDateTime(d) : default,
                PayoutLabel(r.Exchange, r.Code, r.Comment),
                r.Amount,
                r.TargetAccountId))
            .ToList();
    }

    private sealed class CandidateRow
    {
        public int Id { get; set; }
        public DateTime? Date { get; set; }
        public string? Type { get; set; }
        public string? Exchange { get; set; }
        public string? Code { get; set; }
        public string? Comment { get; set; }
        public double? Quantity { get; set; }
        public decimal Amount { get; set; }
        public int TargetAccountId { get; set; }
    }

    private static string PayoutLabel(string? exchange, string? code, string? comment)
    {
        var label = $"{code?.Trim()} {exchange?.Trim()} payout".Trim();
        return string.IsNullOrWhiteSpace(comment) ? label : $"{label} ({comment.Trim()})";
    }

    private static async Task<IReadOnlyList<MatchCandidate>> TradeMatchesAsync(DbConnection con, DbTransaction? tx, int portfolioId, SuspenseLine line, int? tradeId)
    {
        // Settled: the bank account the trade's own cash transaction was paid from or into.
        // Unsettled: Trade Clearing, where the trade's journal left the amount owing.
        var rows = await con.QueryAsync<CandidateRow>("""
            SELECT TOP 20 T.iShareTradeId AS Id, T.dtmDateOfTransaction AS Date, T.strType AS Type, T.strStockExchangeCode AS Exchange,
                   T.strStockCode AS Code, T.iQuantity AS Quantity,
                   CAST(ROUND(ISNULL(T.dblOrderTotal, 0) / ISNULL(NULLIF(T.dblExchangeRate, 0), 1), 2) AS DECIMAL(19,2)) AS Amount,
                   COALESCE(Settled.iAccountID, Clearing.iAccountID) AS TargetAccountId
            FROM tblShareTrades T
            OUTER APPLY (
                SELECT TOP 1 JL.iAccountID
                FROM tblBankStatement B
                JOIN tblJournal J ON J.iUserID = @portfolioId AND J.strSourceModule = 'BankTransaction' AND J.iSourceID = B.iBankStatementId
                JOIN tblJournalLines JL ON JL.iJournalID = J.iJournalID
                JOIN tblChartOfAccounts CA ON CA.iAccountID = JL.iAccountID
                WHERE B.iUserId = @portfolioId AND B.iShareTradeId = T.iShareTradeId
                  AND CA.strAccountName <> 'Trade Clearing / Suspense'
                ORDER BY J.iJournalID, JL.iJournalLineID
            ) Settled
            OUTER APPLY (
                SELECT TOP 1 JL.iAccountID
                FROM tblJournal J
                JOIN tblJournalLines JL ON JL.iJournalID = J.iJournalID
                JOIN tblChartOfAccounts CA ON CA.iAccountID = JL.iAccountID
                WHERE J.iUserID = @portfolioId AND J.strSourceModule = 'ShareTrade' AND J.iSourceID = T.iShareTradeId
                  AND CA.strAccountName = 'Trade Clearing / Suspense'
                ORDER BY J.iJournalID, JL.iJournalLineID
            ) Clearing
            WHERE T.iUserId = @portfolioId
              AND (@tradeId IS NULL OR T.iShareTradeId = @tradeId)
              AND UPPER(T.strType) = @type
              AND ABS(ROUND(ISNULL(T.dblOrderTotal, 0) / ISNULL(NULLIF(T.dblExchangeRate, 0), 1), 2) - @amount) < 0.005
              AND COALESCE(Settled.iAccountID, Clearing.iAccountID) IS NOT NULL
              AND COALESCE(Settled.iAccountID, Clearing.iAccountID) <> @suspenseAccountId
              AND NOT EXISTS (
                  SELECT 1 FROM tblJournal A
                  WHERE A.iUserID = @portfolioId AND A.strSourceModule = 'ManualAllocation'
                    AND A.strDescription LIKE 'Matched to trade ' + CAST(T.iShareTradeId AS VARCHAR(12)) + ':%')
            ORDER BY ABS(DATEDIFF(DAY, T.dtmDateOfTransaction, @date)), T.iShareTradeId DESC
            """, new
            {
                portfolioId,
                tradeId,
                // A withdrawal paid for a buy; a deposit is the proceeds of a sell
                type = line.Line.IsDeposit ? "SELL" : "BUY",
                amount = Math.Abs(line.Line.Amount),
                suspenseAccountId = line.SuspenseAccountId,
                date = line.Line.Date.ToDateTime(TimeOnly.MinValue),
            }, tx);

        return rows.Select(r => new MatchCandidate(
                r.Id,
                r.Date is { } d ? DateOnly.FromDateTime(d) : default,
                $"{r.Type?.Trim()} {Math.Abs(r.Quantity ?? 0):N0} {r.Code?.Trim()} {r.Exchange?.Trim()}".Trim(),
                r.Amount,
                r.TargetAccountId))
            .ToList();
    }

    /// <summary>The procs answer 'Success' or a message starting 'Error:'.</summary>
    public static AllocationResult Result(string? procResult)
    {
        var account = ChartOfAccountsService.Result(procResult);
        return account.Ok ? AllocationResult.Success : new AllocationResult(account.Error);
    }
}
