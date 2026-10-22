using System.Data;
using Dapper;
using PortfolioManager.Core.CorporateActions;
using PortfolioManager.Core.Data;
using PortfolioManager.Core.Tax;
using PortfolioManager.Core.Trades;

namespace PortfolioManager.Core.Accounting;

/// <summary>
/// Keeps each sale's journal posting its realised gain or loss. uspAddShareTrade credits a sale's
/// proceeds to the share's cost base account; this works out the FIFO cost of the units sold
/// (<see cref="SaleCosts"/>) and has uspPostSaleGain move the difference to Realised Gains on
/// Investments. A backdated buy, a deleted trade, a split or a return of capital changes the cost of
/// later sales, so it runs before the ledger is read rather than once per sale, and only rewrites
/// journals that are out of date. Scoped: it runs once per portfolio per request.
/// </summary>
public sealed class RealisedGainService(IDbConnectionFactory db, TradesService trades, CorporateActionsService actions, ParcelChoiceService parcelChoices)
{
    private readonly HashSet<int> _synced = [];

    /// <summary>How many sale journals were rewritten.</summary>
    public async Task<int> SyncAsync(int portfolioId)
    {
        if (!_synced.Add(portfolioId))
            return 0;

        await using var con = db.Create();
        var journals = MapSaleJournals((await con.QueryAsync("uspGetSaleJournals", new { iUserId = portfolioId },
            commandType: CommandType.StoredProcedure)).Cast<IDictionary<string, object?>>());
        if (journals.Count == 0)
            return 0;

        var ledgerTrades = (await trades.GetTradesAsync(portfolioId, null, oldestFirst: true))
            .Select(t => new LedgerTrade(t.TradeId, t.ExchangeCode, t.StockCode, t.Type, DateOnly.FromDateTime(t.Date),
                (decimal)t.Quantity, LedgerTrade.InPortfolioCurrency(t.OrderTotal, (decimal)t.ExchangeRate)));
        var costs = SaleCosts.Match(ledgerTrades, await actions.GetCapitalReturnLotsAsync(portfolioId), await parcelChoices.GetAsync(portfolioId));

        var posted = 0;
        foreach (var j in journals)
        {
            if (!j.NeedsPosting(costs.TryGetValue(j.TradeId, out var c) ? c : (decimal?)null, out var cost))
                continue;
            await con.ExecuteAsync("uspPostSaleGain", new { iUserId = portfolioId, iJournalID = j.JournalId, dblCostBase = cost },
                commandType: CommandType.StoredProcedure);
            posted++;
        }
        return posted;
    }

    public static IReadOnlyList<SaleJournal> MapSaleJournals(IEnumerable<IDictionary<string, object?>> rows) =>
        rows.Select(r => new SaleJournal(
                Db.Int(r, "iShareTradeId") ?? 0,
                Db.Int(r, "iJournalID") ?? 0,
                Db.Int(r, "CostBaseLines") ?? 0,
                Db.Dec(r, "CostPosted") ?? 0,
                Db.Dec(r, "GainPosted") ?? 0))
            .Where(j => j.JournalId > 0)
            .ToList();
}
