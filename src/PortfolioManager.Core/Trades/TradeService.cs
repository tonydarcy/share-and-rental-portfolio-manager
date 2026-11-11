using System.Data;
using Dapper;
using PortfolioManager.Core.Data;

namespace PortfolioManager.Core.Trades;

/// <summary>Records share trades through the same procs as the legacy Add Trade page.</summary>
public sealed class TradeService(IDbConnectionFactory db)
{
    /// <summary>
    /// With a cash settlement this calls AddShareTradeWithCashTransaction (trade, bank entry and,
    /// in Hybrid/DoubleEntry mode, both journals); without one, uspAddShareTrade alone.
    /// The proc runs several statements without its own transaction, so it runs inside one here:
    /// either everything is recorded or nothing is.
    /// </summary>
    /// <summary>Records the trade; returns its id.</summary>
    public async Task<int> AddAsync(int portfolioId, NewTrade trade)
    {
        await using var con = db.Create();
        await con.OpenAsync();
        await using var tx = await con.BeginTransactionAsync();
        var id = await AddAsync(con, tx, portfolioId, trade);
        await tx.CommitAsync();
        return id;
    }

    /// <summary>Units held of one code at the end of a day (buys less sells; splits are already applied to the trades).</summary>
    public async Task<decimal> GetUnitsHeldAsync(int portfolioId, string market, string code, DateOnly date)
    {
        await using var con = db.Create();
        var units = await con.ExecuteScalarAsync<double?>(
            """
            SELECT SUM(CASE UPPER(strType) WHEN 'BUY' THEN iQuantity WHEN 'SELL' THEN -iQuantity ELSE 0 END)
            FROM tblShareTrades
            WHERE iUserId = @portfolioId AND strStockExchangeCode = @market AND strStockCode = @code AND dtmDateOfTransaction <= @date
            """,
            new { portfolioId, market = AnsiText.Of(market), code = AnsiText.Of(code), date = date.ToDateTime(TimeOnly.MinValue) });
        return (decimal)Math.Round(units ?? 0, 8);
    }

    /// <summary>Records the trade inside the caller's transaction, then any securities transaction tax (India) against it.</summary>
    public static async Task<int> AddAsync(IDbConnection con, IDbTransaction tx, int portfolioId, NewTrade trade)
    {
        var (proc, parameters) = trade.Cash is null
            ? ("uspAddShareTrade", TradeEntry.ShareTradeParameters(portfolioId, trade))
            : ("AddShareTradeWithCashTransaction", TradeEntry.ShareTradeWithCashParameters(portfolioId, trade));
        await con.ExecuteAsync(proc, new DynamicParameters(parameters), tx, commandType: CommandType.StoredProcedure);
        if (trade.SecuritiesTax > 0)
            await con.ExecuteAsync("uspSetLatestTradeSecuritiesTax", SecuritiesTaxParameters(portfolioId, trade), tx,
                commandType: CommandType.StoredProcedure);
        // The new trade is the portfolio's newest (the same rule uspSetLatestTradeSecuritiesTax uses), inside this transaction
        return await con.ExecuteScalarAsync<int?>(
            "SELECT MAX(iShareTradeId) FROM dbo.tblShareTrades WHERE iUserId = @portfolioId", new { portfolioId }, tx) ?? 0;
    }

    /// <summary>The STT in the portfolio currency (the exchange rate is trade currency per portfolio currency).</summary>
    public static object SecuritiesTaxParameters(int portfolioId, NewTrade trade) => new
    {
        iPortfolioId = portfolioId,
        dblAmount = Math.Round(trade.SecuritiesTax / (trade.ExchangeRate > 0 ? trade.ExchangeRate : 1m), 2, MidpointRounding.AwayFromZero),
    };

    /// <summary>Securities transaction tax recorded on the portfolio's trades, by trade id (portfolio currency).</summary>
    public async Task<IReadOnlyDictionary<int, decimal>> GetSecuritiesTaxAsync(int portfolioId)
    {
        await using var con = db.Create();
        var rows = await con.QueryAsync<(int TradeId, decimal Amount)>("uspGetTradeSecuritiesTax", new { iPortfolioId = portfolioId },
            commandType: CommandType.StoredProcedure);
        return rows.ToDictionary(r => r.TradeId, r => r.Amount);
    }
}
