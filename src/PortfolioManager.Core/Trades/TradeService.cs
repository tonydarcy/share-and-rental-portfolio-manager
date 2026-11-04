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
    public async Task AddAsync(int portfolioId, NewTrade trade)
    {
        await using var con = db.Create();
        await con.OpenAsync();
        await using var tx = await con.BeginTransactionAsync();
        await AddAsync(con, tx, portfolioId, trade);
        await tx.CommitAsync();
    }

    /// <summary>Records the trade inside the caller's transaction, then any securities transaction tax (India) against it.</summary>
    public static async Task AddAsync(IDbConnection con, IDbTransaction tx, int portfolioId, NewTrade trade)
    {
        var (proc, parameters) = trade.Cash is null
            ? ("uspAddShareTrade", TradeEntry.ShareTradeParameters(portfolioId, trade))
            : ("AddShareTradeWithCashTransaction", TradeEntry.ShareTradeWithCashParameters(portfolioId, trade));
        await con.ExecuteAsync(proc, new DynamicParameters(parameters), tx, commandType: CommandType.StoredProcedure);
        if (trade.SecuritiesTax > 0)
            await con.ExecuteAsync("uspSetLatestTradeSecuritiesTax", SecuritiesTaxParameters(portfolioId, trade), tx,
                commandType: CommandType.StoredProcedure);
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
