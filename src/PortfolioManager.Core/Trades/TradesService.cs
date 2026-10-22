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
