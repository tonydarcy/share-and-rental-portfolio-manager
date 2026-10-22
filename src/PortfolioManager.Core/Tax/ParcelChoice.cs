namespace PortfolioManager.Core.Tax;

/// <summary>
/// How the units of a sale are matched to the parcels held. The ATO accepts any method when the parcels can be
/// identified (e.g. by HIN and purchase date); FIFO is the default and what is used when no choice is saved.
/// </summary>
public enum ParcelMethod { Fifo, Specific, MinGain, MaxGain }

/// <summary>Units taken from the parcel bought by one trade.</summary>
public sealed record ParcelPick(int BuyTradeId, decimal Units);

/// <summary>
/// The parcels one sale took. MinGain and MaxGain are worked out into picks when saved, so every report
/// and journal reads the same picks; Method only records how they were chosen.
/// </summary>
public sealed record SaleParcelChoice(int SellTradeId, ParcelMethod Method, IReadOnlyList<ParcelPick> Picks);

/// <summary>A parcel held just before a sale, as the parcel choice screen shows it.</summary>
public sealed record HeldParcel(int BuyTradeId, DateOnly Acquired, decimal Units, decimal CostPerUnit);

public static class ParcelChoices
{
    public static string Label(ParcelMethod m) => m switch
    {
        ParcelMethod.Specific => "Parcels picked",
        ParcelMethod.MinGain => "Smallest gain",
        ParcelMethod.MaxGain => "Largest gain",
        _ => "First in, first out",
    };

    public static ParcelMethod Parse(string? text) =>
        Enum.TryParse<ParcelMethod>(text?.Trim(), true, out var m) ? m : ParcelMethod.Fifo;

    /// <summary>
    /// The order to take units from the lots held, as (lot index, units). Picks come first, each limited to what the lot
    /// still has; anything a pick can't cover (a parcel since edited or deleted) is taken first-in, first-out, so a
    /// sale is never left without a cost base. With no choice it is plain FIFO.
    /// </summary>
    public static IReadOnlyList<(int Lot, decimal Units)> Take(IReadOnlyList<(int BuyTradeId, decimal Units)> lots, decimal needed, SaleParcelChoice? choice)
    {
        var left = lots.Select(l => l.Units).ToArray();
        var takes = new List<(int, decimal)>();
        foreach (var pick in choice?.Picks ?? [])
        {
            if (needed <= 0) break;
            var i = IndexOf(lots, pick.BuyTradeId);
            if (i < 0 || pick.Units <= 0) continue;
            var units = Math.Min(Math.Min(pick.Units, left[i]), needed);
            if (units <= 0) continue;
            takes.Add((i, units));
            left[i] -= units;
            needed -= units;
        }
        for (var i = 0; i < lots.Count && needed > 0; i++)
        {
            var units = Math.Min(left[i], needed);
            if (units <= 0) continue;
            takes.Add((i, units));
            left[i] -= units;
            needed -= units;
        }
        return takes;
    }

    /// <summary>True when the picks can't all be taken as saved (a parcel changed since), so FIFO filled the gap.</summary>
    public static bool IsStale(IReadOnlyList<(int BuyTradeId, decimal Units)> lots, decimal needed, SaleParcelChoice choice)
    {
        if (choice.Picks.Sum(p => p.Units) != needed)
            return true;
        return choice.Picks.Any(p => IndexOf(lots, p.BuyTradeId) is var i && (i < 0 || lots[i].Units < p.Units));
    }

    /// <summary>
    /// Picks for a method. Smallest gain takes the parcels with the lowest taxable gain per unit first: losses, then
    /// discounted gains, then other gains; largest gain the reverse. Ties go to the older parcel.
    /// </summary>
    public static IReadOnlyList<ParcelPick> Plan(ParcelMethod method, IReadOnlyList<HeldParcel> held, decimal units,
        DateOnly saleDate, decimal proceedsPerUnit, decimal discountRate)
    {
        decimal Taxable(HeldParcel p)
        {
            var gain = proceedsPerUnit - p.CostPerUnit;
            return gain > 0 && CgtDiscount.Qualifies(p.Acquired, saleDate) ? gain * (1 - discountRate) : gain;
        }

        var ordered = method switch
        {
            ParcelMethod.MinGain => held.OrderBy(Taxable).ThenBy(p => p.Acquired).ThenBy(p => p.BuyTradeId),
            ParcelMethod.MaxGain => held.OrderByDescending(Taxable).ThenBy(p => p.Acquired).ThenBy(p => p.BuyTradeId),
            _ => held.OrderBy(p => p.Acquired).ThenBy(p => p.BuyTradeId),
        };
        var picks = new List<ParcelPick>();
        foreach (var p in ordered)
        {
            if (units <= 0) break;
            var take = Math.Min(units, p.Units);
            if (take <= 0) continue;
            picks.Add(new ParcelPick(p.BuyTradeId, take));
            units -= take;
        }
        return picks;
    }

    private static int IndexOf(IReadOnlyList<(int BuyTradeId, decimal Units)> lots, int buyTradeId)
    {
        for (var i = 0; i < lots.Count; i++)
            if (lots[i].BuyTradeId == buyTradeId)
                return i;
        return -1;
    }
}
