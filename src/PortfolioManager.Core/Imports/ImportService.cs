using System.Data;
using Dapper;
using PortfolioManager.Core.Data;

namespace PortfolioManager.Core.Imports;

/// <summary>Writes uploaded share prices and trades through the legacy procs, all or nothing.</summary>
public sealed class ImportService(IDbConnectionFactory db)
{
    /// <summary>Keys (see ShareTradeRow.Key) of the trades already in the portfolio, to warn about re-imports.</summary>
    public async Task<IReadOnlySet<string>> GetExistingTradeKeysAsync(int portfolioId)
    {
        await using var con = db.Create();
        var trades = await con.QueryAsync<(string Exchange, string Code, string Type, DateTime Date, double? Quantity, double? Price)>(
            "SELECT strStockExchangeCode, strStockCode, strType, dtmDateOfTransaction, iQuantity, dblPricePerUnit FROM tblShareTrades WHERE iUserId = @portfolioId",
            new { portfolioId });
        return trades
            .Select(t => ShareTradeRow.TradeKey(t.Exchange, t.Code, t.Type, t.Date, (decimal)(t.Quantity ?? 0), (decimal)(t.Price ?? 0)))
            .ToHashSet();
    }

    /// <summary>
    /// Adds the trades with uspAddShareTrade (as the legacy upload does), or with AddShareTradeWithCashTransaction
    /// when withCash is set so the settlement goes to the bank account too. Both post journals for double-entry portfolios.
    /// </summary>
    public async Task ImportTradesAsync(int portfolioId, IReadOnlyList<ShareTradeRow> trades, bool withCash)
    {
        await using var con = db.Create();
        await con.OpenAsync();
        await using var tx = await con.BeginTransactionAsync();

        foreach (var t in trades)
        {
            if (withCash)
            {
                await con.ExecuteAsync("AddShareTradeWithCashTransaction", new
                {
                    iUserId = portfolioId,
                    strMarket = t.StockExchangeCode,
                    strCode = t.StockCode,
                    strName = t.Name,
                    strType = t.Type,
                    dtmDateOfTransaction = t.Date,
                    iQuantity = t.Quantity,
                    dblPricePerUnit = (double)t.Price,
                    dblBrokerage = (double)t.Brokerage,
                    strCurrency = t.Currency,
                    dblExchangeRate = (double)t.ExchangeRate,
                    strShareTradeComment = t.Comment,
                    // Nulls take the proc's defaults: latest bank account, T+2 settlement, generated comment
                    strBankAccountName = (string?)null,
                    dtmSettlementDate = (DateTime?)null,
                    strCashTransactionComment = (string?)null,
                }, tx, commandType: CommandType.StoredProcedure, commandTimeout: 120);
            }
            else
            {
                await con.ExecuteAsync("uspAddShareTrade", new
                {
                    iUserId = portfolioId,
                    strMarket = t.StockExchangeCode,
                    strCode = t.StockCode,
                    strName = t.Name,
                    strType = t.Type,
                    dtmDateOfTransaction = t.Date,
                    iQuantity = t.Quantity,
                    dblPricePerUnit = (double)t.Price,
                    dblBrokerage = (double)t.Brokerage,
                    strCurrency = t.Currency,
                    dblExchangeRate = (double)t.ExchangeRate,
                    strComment = t.Comment,
                    iBankStatementId = (int?)null,
                }, tx, commandType: CommandType.StoredProcedure);
            }
        }

        await tx.CommitAsync();
    }

    /// <summary>
    /// Market prices for everyone (not one portfolio) with uspUploadSharePrice, which adds new
    /// prices and replaces ones already on file for the same code and date.
    /// </summary>
    public async Task ImportSharePricesAsync(string exchangeCode, IReadOnlyList<SharePriceRow> prices)
    {
        await using var con = db.Create();
        await con.OpenAsync();
        await using var tx = await con.BeginTransactionAsync();

        foreach (var p in prices)
        {
            await con.ExecuteAsync("uspUploadSharePrice", new
            {
                strStockExchangeCode = exchangeCode.Trim().ToUpperInvariant(),
                strStockCode = p.StockCode,
                dtmDetailsAsAtDate = p.Date,
                dblOpenPrice = (double)p.Open,
                dblHighPrice = (double)p.High,
                dblLowPrice = (double)p.Low,
                dblClosePrice = (double)p.Close,
                iVolume = (double)p.Volume,
            }, tx, commandType: CommandType.StoredProcedure);
        }

        await tx.CommitAsync();
    }
}
