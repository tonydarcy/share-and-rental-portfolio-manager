namespace PortfolioManager.Core.Tax;

/// <summary>A buy or sell from tblShareTrades. CostBasisPerUnit is the per-unit amount after brokerage, as the CGT report uses it.</summary>
public sealed record TradeLot(
    int TradeId,
    string ExchangeCode,
    string StockCode,
    string Type,
    DateOnly Date,
    decimal Quantity,
    decimal CostBasisPerUnit);

/// <summary>What's left of one purchase that hasn't been sold.</summary>
public sealed record OpenParcel(string ExchangeCode, string StockCode, DateOnly Acquired, decimal Units, decimal CostPerUnit, int BuyTradeId = 0)
{
    public decimal TotalCost => Math.Round(Units * CostPerUnit, 2, MidpointRounding.AwayFromZero);

    public DateOnly EligibleFrom => CgtDiscount.EligibleFrom(Acquired);

    public bool QualifiesOn(DateOnly date) => CgtDiscount.Qualifies(Acquired, date);
}

/// <summary>Something in the trades that couldn't be matched, e.g. selling more than was bought.</summary>
public sealed record ParcelProblem(DateOnly Date, string Message);

/// <summary>CapitalReturns is how many returns of capital were applied.</summary>
public sealed record ParcelMatch(IReadOnlyList<CgtEvent> Events, IReadOnlyList<OpenParcel> Open, IReadOnlyList<ParcelProblem> Problems, int CapitalReturns = 0);

/// <summary>
/// Matches sales to purchases first-in, first-out, per exchange and stock code, keeping the
/// purchase date of each parcel (GetCapitalGainsTaxSummary only returns an average cost per sale).
/// Splits and consolidations need nothing here: uspApplyStockSplit rewrites the earlier trades
/// in the new units with the same order totals, so the cost base and purchase dates carry over.
/// Demergers and takeovers with rollover (<see cref="ParcelTransfer"/>) replace the new shares' purchase with
/// the parcels they came from, so holdings are matched source first.
/// </summary>
public static class CgtParcels
{
    private sealed class Lot(int buyTradeId, DateOnly acquired, decimal units, decimal cost)
    {
        public int BuyTradeId { get; } = buyTradeId;
        public DateOnly Acquired { get; } = acquired;
        public decimal Units { get; set; } = units;
        public decimal Cost { get; set; } = cost;
    }

    /// <summary>
    /// Trades and returns of capital after upTo are ignored. On the same day buys come before sells,
    /// and a return of capital comes after both (it goes to the units held at the end of the day).
    /// Sell quantities may be negative (Sharesight imports), so their size is used.
    /// A sale with a saved parcel choice takes those parcels (see <see cref="ParcelChoices.Take"/>); others are FIFO.
    /// </summary>
    public static ParcelMatch Match(IEnumerable<TradeLot> trades, DateOnly? upTo = null, IEnumerable<CapitalReturn>? capitalReturns = null,
        IReadOnlyDictionary<int, SaleParcelChoice>? choices = null)
    {
        var events = new List<CgtEvent>();
        var open = new List<OpenParcel>();
        var problems = new List<ParcelProblem>();

        var all = (capitalReturns ?? []).Where(r => upTo is null || r.Date <= upTo).ToList();
        // A takeover's rollover happens at its sale; everything else at its date
        var rollovers = all.Where(r => r.Transfer is { SellTradeId: not null } x && (x.IsRollover || x.IsExercise))
            .GroupBy(r => r.Transfer!.SellTradeId!.Value).ToDictionary(g => g.Key, g => g.First());
        var returns = all
            .Where(r => r.Transfer is null || r.Transfer is { IsDemerger: true, BuyTradeId: not null })
            .GroupBy(r => Key(r.ExchangeCode, r.StockCode))
            .ToDictionary(g => g.Key, g => new Queue<CapitalReturn>(g.OrderBy(r => r.Date)));
        // The new shares' purchase to the parcels it takes over, filled in when the old holding gets there
        var inherited = new Dictionary<int, List<Lot>>();
        var applied = 0;

        var holdings = trades
            .Where(t => upTo is null || t.Date <= upTo)
            .GroupBy(t => Key(t.ExchangeCode, t.StockCode))
            .ToList();

        foreach (var holding in OrderForTransfers(holdings, all))
        {
            var exchange = holding.First().ExchangeCode.Trim();
            var code = holding.First().StockCode.Trim();
            var lots = new List<Lot>();
            returns.Remove(holding.Key, out var pending);

            foreach (var t in holding.OrderBy(t => t.Date).ThenBy(t => IsBuy(t) ? 0 : 1).ThenBy(t => t.TradeId))
            {
                while (pending is { Count: > 0 } && pending.Peek().Date < t.Date)
                    applied += ApplyReturn(pending.Dequeue(), lots, exchange, code, events, problems, inherited) ? 1 : 0;

                var qty = Math.Abs(t.Quantity);
                if (qty == 0)
                    continue;

                if (IsBuy(t))
                {
                    if (inherited.Remove(t.TradeId, out var taken) && taken.Count > 0)
                        lots.AddRange(Scale(taken, qty));
                    else
                        lots.Add(new Lot(t.TradeId, t.Date, qty, t.CostBasisPerUnit));
                    continue;
                }

                if (!t.Type.Trim().Equals("SELL", StringComparison.OrdinalIgnoreCase))
                {
                    problems.Add(new(t.Date, $"A {code} trade on {t.Date:d MMM yyyy} has type \"{t.Type.Trim()}\", so it was left out."));
                    continue;
                }

                var needed = qty;
                var choice = choices?.GetValueOrDefault(t.TradeId);
                var rollover = rollovers.GetValueOrDefault(t.TradeId)?.Transfer;
                // With rollover only the cash counts as proceeds, and the cost base share for the new shares moves to them
                var proceedsPerUnit = rollover is null ? t.CostBasisPerUnit : Math.Max(0m, t.CostBasisPerUnit - rollover.ScripValue / qty);
                var moved = new List<Lot>();
                var exercise = rollover?.IsExercise == true;
                foreach (var (i, units) in ParcelChoices.Take(lots.Select(l => (l.BuyTradeId, l.Units)).ToList(), needed, choice))
                {
                    var lot = lots[i];
                    var cost = units * lot.Cost;
                    if (rollover is not null && !exercise)
                    {
                        var share = cost * rollover.CostShare;
                        moved.Add(new Lot(lot.BuyTradeId, lot.Acquired, units * rollover.NewPerOld, share));
                        cost -= share;
                    }
                    if (!exercise && (rollover is null || cost != 0 || proceedsPerUnit != 0))
                        events.Add(new CgtEvent(exchange, code, lot.Acquired, t.Date, units,
                            Cents(cost), Cents(units * proceedsPerUnit), SellTradeId: t.TradeId, BuyTradeId: lot.BuyTradeId));
                    lot.Units -= units;
                    needed -= units;
                }
                lots.RemoveAll(l => l.Units == 0);
                if (!exercise && rollover?.BuyTradeId is int buy)
                    inherited[buy] = moved;

                if (needed > 0)
                    problems.Add(new(t.Date, $"{needed:#,##0.########} {code} units sold on {t.Date:d MMM yyyy} are more than were held, so they have no cost base and were left out."));
            }

            while (pending is { Count: > 0 })
                applied += ApplyReturn(pending.Dequeue(), lots, exchange, code, events, problems, inherited) ? 1 : 0;

            open.AddRange(lots.Select(l => new OpenParcel(exchange, code, l.Acquired, l.Units, l.Cost, l.BuyTradeId)));
        }

        // Returns of capital on holdings with no trades at all
        foreach (var r in returns.Values.SelectMany(q => q))
            problems.Add(NoUnitsHeld(r, r.StockCode.Trim()));

        return new ParcelMatch(
            events.OrderBy(e => e.Disposed).ThenBy(e => e.StockCode, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Acquired).ToList(),
            open.OrderBy(p => p.StockCode, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.ExchangeCode, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Acquired).ToList(),
            problems.OrderBy(p => p.Date).ToList(),
            applied);
    }

    /// <summary>A holding that receives a demerger's or takeover's parcels comes after the one they come from (chains too).</summary>
    private static IEnumerable<IGrouping<(string, string), TradeLot>> OrderForTransfers(List<IGrouping<(string, string), TradeLot>> holdings, List<CapitalReturn> all)
    {
        var links = all.Where(r => r.Transfer is { } x && (x.IsDemerger || x.IsRollover))
            .Select(r => (From: Key(r.ExchangeCode, r.StockCode), To: Key(r.Transfer!.ToExchangeCode, r.Transfer.ToCode)))
            .Where(l => l.From != l.To)
            .ToList();
        if (links.Count == 0)
            return holdings;
        var depth = holdings.ToDictionary(h => h.Key, _ => 0);
        // Each pass pushes a receiver below its source; a loop in the data stops after one pass per holding
        for (var pass = 0; pass < holdings.Count; pass++)
        {
            var changed = false;
            foreach (var (from, to) in links)
                if (depth.TryGetValue(from, out var d) && depth.TryGetValue(to, out var e) && e <= d)
                {
                    depth[to] = d + 1;
                    changed = true;
                }
            if (!changed)
                break;
        }
        return holdings.OrderBy(h => depth[h.Key]);
    }

    /// <summary>Parcels taken over by a purchase, in its units: each in proportion, the last taking what rounding leaves.</summary>
    private static IEnumerable<Lot> Scale(List<Lot> taken, decimal qty)
    {
        var total = taken.Sum(l => l.Units);
        var left = qty;
        for (var i = 0; i < taken.Count; i++)
        {
            var l = taken[i];
            var units = i == taken.Count - 1 ? left : (total == 0 ? 0 : Math.Round(qty * l.Units / total, 4, MidpointRounding.AwayFromZero));
            left -= units;
            // Lot.Cost holds the total cost moved until now, then a cost per unit
            if (units > 0)
                yield return new Lot(l.BuyTradeId, l.Acquired, units, l.Cost / units);
        }
    }

    /// <summary>
    /// Reduces the cost base of every parcel held, pro rata by units. A parcel whose cost base
    /// runs out keeps a cost base of nil, and the rest of its share is a G1 capital gain.
    /// </summary>
    private static bool ApplyReturn(CapitalReturn r, List<Lot> lots, string exchange, string code, List<CgtEvent> events, List<ParcelProblem> problems,
        Dictionary<int, List<Lot>> inherited)
    {
        // A demerger: each parcel keeps its share of the cost base and passes the rest, with its purchase date, to the new shares
        if (r.Transfer is { IsDemerger: true, BuyTradeId: int buy } d)
        {
            if (lots.Count == 0)
            {
                problems.Add(new(r.Date, $"The {code} demerger on {r.Date:d MMM yyyy} was recorded when no units were held, so the new shares keep their own cost base."));
                return false;
            }
            inherited[buy] = lots.Select(l => new Lot(l.BuyTradeId, l.Acquired, l.Units * d.NewPerOld, l.Units * l.Cost * d.CostShare)).ToList();
            foreach (var l in lots)
                l.Cost *= 1 - d.CostShare;
            return false;
        }

        // A fund's distributed gains count whether or not units are still held at the year end
        if (r.DiscountGain > 0)
            events.Add(new CgtEvent(exchange, code, r.Date.AddYears(-2), r.Date, 0m, 0m, r.DiscountGain, CgtEventType.FundCapitalGain));
        if (r.OtherGain > 0)
            events.Add(new CgtEvent(exchange, code, r.Date, r.Date, 0m, 0m, r.OtherGain, CgtEventType.FundCapitalGain));
        if (r.FromStatement && r.Amount == 0)
            return false;

        var shares = CapitalReturns.Apportion(lots.Select(l => (l.Units, l.Units * l.Cost)).ToList(), r.Amount);
        if (shares.Count == 0)
        {
            problems.Add(NoUnitsHeld(r, code));
            return false;
        }

        for (var i = 0; i < lots.Count; i++)
        {
            var lot = lots[i];
            var s = shares[i];
            lot.Cost = s.CostBaseAfter / lot.Units;
            if (s.Excess > 0)
                events.Add(new CgtEvent(exchange, code, lot.Acquired, r.Date, lot.Units, 0m, s.Excess, CgtEventType.CapitalReturnExcess, BuyTradeId: lot.BuyTradeId));
        }
        return !r.FromStatement;
    }

    private static ParcelProblem NoUnitsHeld(CapitalReturn r, string code) => r.FromStatement
        ? new(r.Date, $"The {code} tax statement's cost base {(r.Amount > 0 ? "decrease" : "increase")} of {Math.Abs(r.Amount):C} is for {r.Date:d MMM yyyy}, when no units were held, so it was left out.")
        : new(r.Date, $"The {code} return of capital of {r.Amount:C} on {r.Date:d MMM yyyy} was paid when no units were held, so it was left out.");

    private static (string Exchange, string Code) Key(string exchange, string code) =>
        (exchange.Trim().ToUpperInvariant(), code.Trim().ToUpperInvariant());

    private static bool IsBuy(TradeLot t) => t.Type.Trim().Equals("BUY", StringComparison.OrdinalIgnoreCase);

    private static decimal Cents(decimal amount) => Math.Round(amount, 2, MidpointRounding.AwayFromZero);
}
