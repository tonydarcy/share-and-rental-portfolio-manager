using System.Data;
using Dapper;
using PortfolioManager.Core.Data;

namespace PortfolioManager.Core.Trades;

public sealed record ShareTrade(
    int TradeId,
    DateTime Date,
    string Type,
    string ExchangeCode,
    string StockCode,
    string Name,
    double Quantity,
    double PricePerUnit,
    double Brokerage,
    string Currency,
    double ExchangeRate,
    decimal OrderTotal,
    double CostBasisPerUnit,
    string Comment);

/// <summary>What else in the database points at a trade, worked out before deleting it.</summary>
public sealed record TradeDeleteCheck(int CgtParcels, int ReinvestedPayouts, int Journals, int CashTransactions, int Groups);

public enum TradeDeleteOutcome { Deleted, NotFound, Blocked }

public sealed record TradeDeleteResult(TradeDeleteOutcome Outcome, string? Reason = null);

/// <summary>The share trades list and delete from the legacy Default page.</summary>
public sealed class TradesService(IDbConnectionFactory db)
{
    private const string TradeColumns = """
        iShareTradeId AS TradeId, dtmDateOfTransaction AS Date, strType AS Type,
        strStockExchangeCode AS ExchangeCode, strStockCode AS StockCode, strName AS Name,
        ISNULL(iQuantity, 0) AS Quantity, ISNULL(dblPricePerUnit, 0) AS PricePerUnit,
        ISNULL(dblBrokerage, 0) AS Brokerage, strCurrency AS Currency,
        ISNULL(dblExchangeRate, 1) AS ExchangeRate, ISNULL(dblOrderTotal, 0) AS OrderTotal,
        ISNULL(dblCostBasisPerUnit, 0) AS CostBasisPerUnit, strComment AS Comment
        """;

    /// <summary>The stock codes this portfolio has traded, for the filter.</summary>
    public async Task<IReadOnlyList<string>> GetStockCodesAsync(int portfolioId)
    {
        await using var con = db.Create();
        var codes = await con.QueryAsync<string>(
            "SELECT DISTINCT strStockCode FROM tblShareTrades WHERE iUserId = @portfolioId ORDER BY strStockCode",
            new { portfolioId });
        return codes.Select(c => c.Trim()).Distinct().ToList();
    }

    /// <summary>Trades by date (newest first unless oldestFirst), optionally for one stock code.</summary>
    public async Task<IReadOnlyList<ShareTrade>> GetTradesAsync(int portfolioId, string? stockCode, bool oldestFirst)
    {
        var order = oldestFirst
            ? "dtmDateOfTransaction ASC, iShareTradeId ASC"
            : "dtmDateOfTransaction DESC, iShareTradeId DESC";
        await using var con = db.Create();
        var trades = await con.QueryAsync<ShareTrade>(
            $"SELECT {TradeColumns} FROM tblShareTrades " +
            "WHERE iUserId = @portfolioId AND (@stockCode IS NULL OR strStockCode = @stockCode) " +
            $"ORDER BY {order}",
            new { portfolioId, stockCode });
        return trades.ToList();
    }

    /// <summary>One trade, only if it belongs to the portfolio.</summary>
    public async Task<ShareTrade?> GetTradeAsync(int portfolioId, int tradeId)
    {
        await using var con = db.Create();
        return await con.QuerySingleOrDefaultAsync<ShareTrade>(
            $"SELECT {TradeColumns} FROM tblShareTrades WHERE iUserId = @portfolioId AND iShareTradeId = @tradeId",
            new { portfolioId, tradeId });
    }

    public async Task<TradeDeleteCheck> CheckDeleteAsync(int portfolioId, int tradeId)
    {
        await using var con = db.Create();
        return await CheckDeleteAsync(con, null, portfolioId, tradeId);
    }

    /// <summary>
    /// Deletes a trade with uspDeleteShareTrade, which in one transaction also removes its journal,
    /// the cash transaction recorded with it and that cash transaction's settlement journal.
    /// Trades matched to CGT parcels or created by a DRP are refused (by the proc too).
    /// </summary>
    public async Task<TradeDeleteResult> DeleteTradeAsync(int portfolioId, int tradeId)
    {
        await using var con = db.Create();
        await con.OpenAsync();

        if (await GetTradeAsync(portfolioId, tradeId) is null)
            return new(TradeDeleteOutcome.NotFound);

        var check = await CheckDeleteAsync(con, null, portfolioId, tradeId);
        if (BlockReason(check) is { } reason)
            return new(TradeDeleteOutcome.Blocked, reason);

        // The proc checks again that the trade belongs to the portfolio
        var result = await con.ExecuteScalarAsync<string>("uspDeleteShareTrade",
            new { iUserId = portfolioId, iShareTradeID = tradeId },
            commandType: CommandType.StoredProcedure);
        return ResultFromProc(result);
    }

    /// <summary>The cash transaction recorded with a trade, if any.</summary>
    public async Task<TradeCash?> GetTradeCashAsync(int portfolioId, int tradeId)
    {
        await using var con = db.Create();
        return await con.QueryFirstOrDefaultAsync<TradeCash>("""
            SELECT strBankAccountName AS BankAccount, CAST(dtmDateOfTransaction AS datetime) AS Date, strComment AS Comment
            FROM tblBankStatement WHERE iUserId = @portfolioId AND iShareTradeId = @tradeId
            ORDER BY iBankStatementId
            """, new { portfolioId, tradeId });
    }

    /// <summary>
    /// Changes a trade. The database has no proc to edit one, so in one transaction this deletes it with
    /// uspDeleteShareTrade (its journals and cash settlement go too), records the new version with the same
    /// proc as Add trade (posting fresh journals and, if asked, the settlement), and puts back its custom group
    /// links. The trade gets a new id. Anything that would stop a delete, or that points at the old id
    /// (splits, parcel transfers, saved parcel choices), stops the edit and nothing changes.
    /// </summary>
    public async Task<TradeEditResult> ReplaceTradeAsync(int portfolioId, int tradeId, NewTrade trade)
    {
        await using var con = db.Create();
        await con.OpenAsync();
        await using var tx = await con.BeginTransactionAsync();

        var exists = await con.ExecuteScalarAsync<int?>(
            "SELECT 1 FROM tblShareTrades WITH (UPDLOCK, HOLDLOCK) WHERE iUserId = @portfolioId AND iShareTradeId = @tradeId",
            new { portfolioId, tradeId }, tx);
        if (exists is null)
            return new(TradeEditOutcome.NotFound);

        var editCheck = await con.QuerySingleAsync<TradeEditCheck>("""
            SELECT
                (SELECT COUNT(*) FROM tblCorporateActionTrades WHERE iShareTradeId = @tradeId) AS SplitAdjustments,
                (SELECT COUNT(*) FROM tblParcelTransfers WHERE iUserID = @portfolioId AND (iBuyTradeID = @tradeId OR iSellTradeID = @tradeId)) AS ParcelTransfers,
                (SELECT COUNT(*) FROM tblSaleParcelChoice WHERE iUserID = @portfolioId AND iSellTradeID = @tradeId)
                + (SELECT COUNT(*) FROM tblSaleParcelAllocation WHERE iUserID = @portfolioId AND (iBuyTradeID = @tradeId OR iSellTradeID = @tradeId)) AS ParcelChoices
            """, new { portfolioId, tradeId }, tx);
        if ((BlockReason(await CheckDeleteAsync(con, tx, portfolioId, tradeId)) ?? TradeEdit.BlockReason(editCheck)) is { } reason)
            return new(TradeEditOutcome.Blocked, reason);

        var groups = (await con.QueryAsync<int>(
            "SELECT iGroupID FROM tblShareTradeGroups WHERE iShareTradeID = @tradeId", new { tradeId }, tx)).ToList();

        var deleted = ResultFromProc(await con.ExecuteScalarAsync<string>("uspDeleteShareTrade",
            new { iUserId = portfolioId, iShareTradeID = tradeId }, tx, commandType: CommandType.StoredProcedure));
        if (deleted.Outcome != TradeDeleteOutcome.Deleted)
            return new(TradeEditOutcome.Blocked, deleted.Reason);

        await TradeService.AddAsync(con, tx, portfolioId, trade);

        // Neither add proc returns the new id; it is this portfolio's newest trade, still locked by this transaction
        var newId = await con.ExecuteScalarAsync<int>(
            "SELECT MAX(iShareTradeId) FROM tblShareTrades WHERE iUserId = @portfolioId AND strStockCode = @code",
            new { portfolioId, code = trade.Code }, tx);
        foreach (var groupId in groups)
            await con.ExecuteAsync("uspAddTradeToGroup", new { iUserId = portfolioId, iShareTradeID = newId, iGroupID = groupId },
                tx, commandType: CommandType.StoredProcedure);

        await tx.CommitAsync();
        return new(TradeEditOutcome.Saved, NewTradeId: newId);
    }

    /// <summary>Reads uspDeleteShareTrade's one-row message.</summary>
    public static TradeDeleteResult ResultFromProc(string? result)
    {
        var text = result?.Trim() ?? "";
        if (text.StartsWith("Error:", StringComparison.OrdinalIgnoreCase))
            return new(TradeDeleteOutcome.Blocked, text["Error:".Length..].Trim());
        if (text.StartsWith("Share Trade Deleted", StringComparison.OrdinalIgnoreCase))
            return new(TradeDeleteOutcome.Deleted);
        return new(TradeDeleteOutcome.Blocked, "The trade could not be deleted. Nothing was changed.");
    }

    private static Task<TradeDeleteCheck> CheckDeleteAsync(IDbConnection con, IDbTransaction? tx, int portfolioId, int tradeId) =>
        con.QuerySingleAsync<TradeDeleteCheck>("""
            SELECT
                (SELECT COUNT(*) FROM tblCGTParcelMatching WHERE iBuyTradeID = @tradeId OR iSellTradeID = @tradeId) AS CgtParcels,
                (SELECT COUNT(*) FROM tblPayouts WHERE iReinvestmentTradeID = @tradeId) AS ReinvestedPayouts,
                (SELECT COUNT(*) FROM tblJournal
                  WHERE iUserID = @portfolioId
                    AND ((strSourceModule = 'ShareTrade' AND iSourceID = @tradeId)
                      OR (strSourceModule = 'BankTransaction' AND iSourceID IN
                            (SELECT iBankStatementId FROM tblBankStatement WHERE iUserId = @portfolioId AND iShareTradeId = @tradeId)))) AS Journals,
                (SELECT COUNT(*) FROM tblBankStatement WHERE iUserId = @portfolioId AND iShareTradeId = @tradeId) AS CashTransactions,
                (SELECT COUNT(*) FROM tblShareTradeGroups WHERE iShareTradeID = @tradeId) AS Groups
            """, new { portfolioId, tradeId }, tx);

    /// <summary>Why a trade can't be deleted safely, or null when it can.</summary>
    public static string? BlockReason(TradeDeleteCheck check)
    {
        if (check.ReinvestedPayouts > 0)
            return "This trade was created by a dividend reinvestment (DRP). Delete the payout instead.";
        if (check.CgtParcels > 0)
            return "This trade is matched to CGT parcels. It can't be deleted until those matches are recalculated without it.";
        return null;
    }
}
