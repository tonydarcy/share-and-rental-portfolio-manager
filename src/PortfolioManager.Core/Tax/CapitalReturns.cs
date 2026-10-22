namespace PortfolioManager.Core.Tax;

/// <summary>A return of capital paid on a holding (tblReturnsOfCapital). Amount is the total cash received.</summary>
/// <summary>
/// A change to the cost base of every parcel held on a date: a return of capital (Amount reduces it), or a fund's annual
/// tax statement (<see cref="FromStatement"/>), whose AMIT net increase is a negative Amount and whose capital gains are
/// gains of the year on that date, discount method gains grossed up.
/// </summary>
/// A <see cref="Transfer"/> (a demerger or a takeover with rollover, Amount nil) moves part of the cost base to another holding.
public sealed record CapitalReturn(string ExchangeCode, string StockCode, DateOnly Date, decimal Amount,
    decimal DiscountGain = 0, decimal OtherGain = 0, bool FromStatement = false, ParcelTransfer? Transfer = null);

/// <summary>
/// Part of a holding's cost base moving to new shares (tblParcelTransfers), which keep each parcel's purchase date.
/// A demerger moves <see cref="CostShare"/> of every parcel held on its date to the new company's purchase
/// <see cref="BuyTradeId"/>; a takeover with scrip-for-scrip rollover does it for the parcels its sale
/// <see cref="SellTradeId"/> takes, and only the cash part of that sale (its proceeds less <see cref="ScripValue"/>) counts.
/// Exercising options sells them for their cost base, which the shares' purchase includes, so that sale is no event.
/// </summary>
public sealed record ParcelTransfer(string Kind, string ToExchangeCode, string ToCode, decimal NewPerOld, decimal CostShare,
    decimal ScripValue = 0, int? BuyTradeId = null, int? SellTradeId = null)
{
    public const string Demerger = "Demerger", Takeover = "Takeover", TakeoverTaxed = "TakeoverTaxed", CodeChange = "CodeChange",
        OptionExercise = "OptionExercise";

    public bool IsDemerger => Kind.Equals(Demerger, StringComparison.OrdinalIgnoreCase);

    public bool IsRollover => Kind.Equals(Takeover, StringComparison.OrdinalIgnoreCase);

    /// <summary>Options or warrants exercised: their sale is no CGT event, as their cost base is already in the shares' purchase.</summary>
    public bool IsExercise => Kind.Equals(OptionExercise, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// One parcel's part of a return of capital. The cost base is reduced by up to its whole amount;
/// anything more is the excess, a capital gain (CGT event G1).
/// </summary>
public sealed record CapitalReturnShare(decimal Units, decimal CostBaseBefore, decimal Share, decimal Reduction, decimal Excess)
{
    public decimal CostBaseAfter => CostBaseBefore - Reduction;
}

/// <summary>How a return of capital is spread over the parcels held when it's paid.</summary>
public static class CapitalReturns
{
    /// <summary>
    /// Splits the amount over the parcels by units, in cents; the last parcel takes the rounding so
    /// the shares add up to the amount exactly. Empty when no units are held.
    /// </summary>
    public static IReadOnlyList<CapitalReturnShare> Apportion(IReadOnlyList<(decimal Units, decimal CostBase)> parcels, decimal amount)
    {
        var totalUnits = parcels.Sum(p => p.Units);
        if (totalUnits <= 0 || parcels.Any(p => p.Units <= 0))
            return [];

        var shares = new List<CapitalReturnShare>(parcels.Count);
        var allocated = 0m;
        for (var i = 0; i < parcels.Count; i++)
        {
            var (units, costBase) = parcels[i];
            var share = i == parcels.Count - 1 ? amount - allocated : Cents(amount * units / totalUnits);
            allocated += share;
            var reduction = Math.Min(share, Math.Max(costBase, 0m));
            shares.Add(new CapitalReturnShare(units, costBase, share, reduction, share - reduction));
        }
        return shares;
    }

    /// <summary>The total paid for an amount per unit, to the cent.</summary>
    public static decimal TotalFromPerUnit(decimal perUnit, decimal unitsHeld) => Cents(perUnit * unitsHeld);

    /// <summary>
    /// Units held on a date in that day's terms. Recorded splits change the trades before them, so
    /// units worked out from the trades are in today's terms; a split after the date is divided back out.
    /// </summary>
    public static decimal UnitsOnTheDay(decimal unitsInTodaysTerms, IEnumerable<decimal> laterSplitFactors) =>
        laterSplitFactors.Aggregate(unitsInTodaysTerms, (units, factor) => factor > 0 ? units / factor : units);

    private static decimal Cents(decimal amount) => Math.Round(amount, 2, MidpointRounding.AwayFromZero);
}
