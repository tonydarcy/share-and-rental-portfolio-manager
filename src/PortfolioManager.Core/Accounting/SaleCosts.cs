using PortfolioManager.Core.Tax;

namespace PortfolioManager.Core.Accounting;

/// <summary>A buy or sell in the amounts its journal posts: OrderTotal is what uspAddShareTrade debits or credits, in the portfolio's currency.</summary>
public sealed record LedgerTrade(int TradeId, string ExchangeCode, string StockCode, string Type, DateOnly Date, decimal Quantity, decimal OrderTotal)
{
    /// <summary>A trade's order total as its journal posts it: divided by the exchange rate (foreign currency per unit of the portfolio's), to the cent.</summary>
    public static decimal InPortfolioCurrency(decimal orderTotal, decimal exchangeRate) =>
        exchangeRate > 0 && exchangeRate != 1 ? Math.Round(orderTotal / exchangeRate, 2, MidpointRounding.AwayFromZero) : orderTotal;
}

/// <summary>
/// The FIFO cost of each sale in ledger amounts, so a sale's journal can credit the cost base account
/// with what the units sold cost and post the rest as a realised gain or loss. It follows
/// <see cref="CgtParcels"/> (same order, returns of capital reduce the parcels held), but keeps each
/// parcel's total cost rather than a cost per unit: a part sale takes its share to the cent and the
/// last units sold take what's left, so a holding sold in full leaves exactly nil in its cost base account.
/// </summary>
public static class SaleCosts
{
    private sealed class Lot(int buyTradeId, decimal units, decimal cost)
    {
        public int BuyTradeId { get; } = buyTradeId;
        public decimal Units { get; set; } = units;
        public decimal Cost { get; set; } = cost;
    }

    /// <summary>
    /// Sale trade id to the cost of the units sold. A sale of more units than were held is left out,
    /// since it has no full cost base; its journal stays at proceeds.
    /// </summary>
    public static IReadOnlyDictionary<int, decimal> Match(IEnumerable<LedgerTrade> trades, IEnumerable<CapitalReturn>? capitalReturns = null,
        IReadOnlyDictionary<int, SaleParcelChoice>? choices = null)
    {
        var costs = new Dictionary<int, decimal>();
        var returns = (capitalReturns ?? [])
            .GroupBy(r => Key(r.ExchangeCode, r.StockCode))
            .ToDictionary(g => g.Key, g => new Queue<CapitalReturn>(g.OrderBy(r => r.Date)));

        foreach (var holding in trades.GroupBy(t => Key(t.ExchangeCode, t.StockCode)))
        {
            var lots = new List<Lot>();
            returns.Remove(holding.Key, out var pending);

            foreach (var t in holding.OrderBy(t => t.Date).ThenBy(t => IsType(t, "BUY") ? 0 : 1).ThenBy(t => t.TradeId))
            {
                while (pending is { Count: > 0 } && pending.Peek().Date < t.Date)
                    ApplyReturn(pending.Dequeue(), lots);

                var qty = Math.Abs(t.Quantity);
                if (qty == 0)
                    continue;

                if (IsType(t, "BUY"))
                {
                    lots.Add(new Lot(t.TradeId, qty, Math.Round(t.OrderTotal, 4, MidpointRounding.AwayFromZero)));
                    continue;
                }
                if (!IsType(t, "SELL"))
                    continue;

                var needed = qty;
                var cost = 0m;
                // The same parcels as CgtParcels takes for this sale
                foreach (var (i, units) in ParcelChoices.Take(lots.Select(l => (l.BuyTradeId, l.Units)).ToList(), needed, choices?.GetValueOrDefault(t.TradeId)))
                {
                    var lot = lots[i];
                    var part = units == lot.Units ? lot.Cost : Cents(lot.Cost * units / lot.Units);
                    cost += part;
                    lot.Cost -= part;
                    lot.Units -= units;
                    needed -= units;
                }
                lots.RemoveAll(l => l.Units == 0);

                if (needed == 0)
                    costs[t.TradeId] = cost;
            }
        }
        return costs;
    }

    /// <summary>A return of capital comes off the parcels held, pro rata by units, down to nil (as CgtParcels does).</summary>
    private static void ApplyReturn(CapitalReturn r, List<Lot> lots)
    {
        // A demerger's journal takes this share of the cost to the new shares' purchase; a takeover is a sale here
        if (r.Transfer is { } t)
        {
            if (t.IsDemerger)
                foreach (var l in lots)
                    l.Cost = Cents(l.Cost * (1 - t.CostShare));
            return;
        }
        var shares = CapitalReturns.Apportion(lots.Select(l => (l.Units, l.Cost)).ToList(), r.Amount);
        for (var i = 0; i < shares.Count; i++)
            lots[i].Cost = shares[i].CostBaseAfter;
    }

    private static (string, string) Key(string exchange, string code) =>
        (exchange.Trim().ToUpperInvariant(), code.Trim().ToUpperInvariant());

    private static bool IsType(LedgerTrade t, string type) => t.Type.Trim().Equals(type, StringComparison.OrdinalIgnoreCase);

    private static decimal Cents(decimal amount) => Math.Round(amount, 2, MidpointRounding.AwayFromZero);
}

/// <summary>A sale's journal as uspGetSaleJournals returns it.</summary>
public sealed record SaleJournal(int TradeId, int JournalId, int CostBaseLines, decimal CostPosted, decimal GainPosted)
{
    /// <summary>The sale's proceeds: the journal balances, so cost plus gain is what the clearing account was debited.</summary>
    public decimal Proceeds => CostPosted + GainPosted;

    /// <summary>
    /// The cost to post (null to put it back to proceeds only) when the journal doesn't already post
    /// this sale's FIFO cost; otherwise no change. A journal without exactly one cost base line is left alone.
    /// </summary>
    public bool NeedsPosting(decimal? fifoCost, out decimal? costToPost)
    {
        costToPost = fifoCost;
        if (CostBaseLines != 1)
            return false;
        return fifoCost is { } cost
            ? Math.Abs(CostPosted - cost) >= 0.0001m
            : GainPosted != 0;
    }
}
