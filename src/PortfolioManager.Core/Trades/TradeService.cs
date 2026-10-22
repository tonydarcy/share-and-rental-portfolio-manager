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
        var (proc, parameters) = trade.Cash is null
            ? ("uspAddShareTrade", TradeEntry.ShareTradeParameters(portfolioId, trade))
            : ("AddShareTradeWithCashTransaction", TradeEntry.ShareTradeWithCashParameters(portfolioId, trade));

        await using var con = db.Create();
        await con.OpenAsync();
        await using var tx = await con.BeginTransactionAsync();
        await con.ExecuteAsync(proc, new DynamicParameters(parameters), tx, commandType: CommandType.StoredProcedure);
        await tx.CommitAsync();
    }
}
