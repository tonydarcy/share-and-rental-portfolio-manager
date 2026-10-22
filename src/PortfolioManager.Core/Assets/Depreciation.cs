using PortfolioManager.Core.Regions;
using PortfolioManager.Core.Tax;

namespace PortfolioManager.Core.Assets;

public static class DepreciationMethods
{
    public const string DiminishingValue = "DV", PrimeCost = "PC", LowValuePool = "LowValue", SmallBusinessPool = "SbPool", WriteOff = "WriteOff",
        None = "None";

    public static readonly IReadOnlyList<string> All = [DiminishingValue, PrimeCost, LowValuePool, SmallBusinessPool, WriteOff, None];

    public static string Label(string method) => method switch
    {
        DiminishingValue => "Diminishing value",
        PrimeCost => "Prime cost",
        LowValuePool => "Low-value pool",
        SmallBusinessPool => "Small business pool",
        WriteOff => "Instant asset write-off",
        _ => "Not depreciated",
    };

    public static bool NeedsLife(string method) => method is DiminishingValue or PrimeCost;

    /// <summary>The pool methods and the write-off are Australian tax rules.</summary>
    public static bool IsAustralianOnly(string method) => method is LowValuePool or SmallBusinessPool or WriteOff;
}

/// <summary>
/// What the depreciation engine needs to know about an asset. OpeningDate and OpeningAccum are for an asset brought in part
/// way through its life: the depreciation to that date, after which the engine carries on.
/// </summary>
public sealed record DepreciableAsset(decimal Cost, string Method, decimal? LifeYears, DateOnly Start, DateOnly? OpeningDate = null,
    decimal OpeningAccum = 0, DateOnly? Disposed = null);

/// <summary>
/// The depreciation rules shared by the fixed asset register and the rental property schedule. Amounts are worked out a
/// financial year at a time, so a year's monthly postings add up to the year's deduction.
/// </summary>
public static class DepreciationEngine
{
    /// <summary>Low-value pool: 18.75% in the year an asset is added, 37.5% of the opening balance after that.</summary>
    public const decimal LowValueFirstRate = 0.1875m, LowValueRate = 0.375m;

    /// <summary>Small business pool: 15% in the year an asset is added, 30% of the opening balance after that.</summary>
    public const decimal SmallBusinessFirstRate = 0.15m, SmallBusinessRate = 0.30m;

    /// <summary>Instant asset write-off: the small business threshold per asset (cost less than this).</summary>
    public const decimal WriteOffThreshold = 20_000m;

    /// <summary>A low-value pool asset costs (or has a written-down value) of less than this.</summary>
    public const decimal LowValueThreshold = 1_000m;

    /// <summary>Diminishing value: opening value × days/365 × 200% / effective life. Not rounded.</summary>
    public static decimal Diminishing(decimal opening, int days, decimal lifeYears) =>
        days <= 0 || lifeYears <= 0 ? 0 : opening * days / 365m * 2m / lifeYears;

    /// <summary>Prime cost: cost × days/365 × 100% / effective life. Not rounded.</summary>
    public static decimal Prime(decimal cost, int days, decimal lifeYears) =>
        days <= 0 || lifeYears <= 0 ? 0 : cost * days / 365m / lifeYears;

    /// <summary>
    /// One financial year's deduction for an asset held for <paramref name="days"/> of it, from its value at the year's start.
    /// Pools take their rate on the year's opening value whatever the days; the first year is the year it was added.
    /// Never more than what is left.
    /// </summary>
    public static decimal YearAmount(DepreciableAsset a, decimal opening, int days, bool firstYear)
    {
        if (days <= 0 || opening <= 0)
            return 0;
        var amount = a.Method switch
        {
            DepreciationMethods.DiminishingValue => Diminishing(opening, days, a.LifeYears ?? 0),
            DepreciationMethods.PrimeCost => Prime(a.Cost, days, a.LifeYears ?? 0),
            DepreciationMethods.LowValuePool => opening * (firstYear ? LowValueFirstRate : LowValueRate),
            DepreciationMethods.SmallBusinessPool => opening * (firstYear ? SmallBusinessFirstRate : SmallBusinessRate),
            DepreciationMethods.WriteOff => opening,
            _ => 0,
        };
        return Math.Min(Math.Round(amount, 2, MidpointRounding.AwayFromZero), opening);
    }

    /// <summary>
    /// The total depreciation from the start to the end of <paramref name="to"/> (or the disposal date, if earlier),
    /// including any opening depreciation. Part of a year is the year's deduction × the days to date / the days held in the year.
    /// </summary>
    public static decimal Accumulated(DepreciableAsset a, DateOnly to, YearStart? yearStart = null)
    {
        var accumulated = Math.Min(a.OpeningAccum, a.Cost);
        if (a.Method == DepreciationMethods.None)
            return accumulated;
        var begin = a.OpeningDate is { } o ? o.AddDays(1) : a.Start;
        var end = a.Disposed is { } d && d < to ? d : to;
        if (end < begin)
            return accumulated;
        if (a.Method == DepreciationMethods.WriteOff)
            return a.Cost;
        var value = a.Cost - accumulated;
        var firstYear = FinancialYear.EndYearFor(a.Start, yearStart);
        for (var y = FinancialYear.EndYearFor(begin, yearStart); y <= FinancialYear.EndYearFor(end, yearStart) && value > 0; y++)
        {
            var ys = FinancialYear.StartDate(y, yearStart);
            var ye = FinancialYear.EndDate(y, yearStart);
            var from = begin > ys ? begin : ys;
            var heldTo = a.Disposed is { } disposed && disposed < ye ? disposed : ye;
            var daysHeld = heldTo.DayNumber - from.DayNumber + 1;
            var yearAmount = YearAmount(a, value, daysHeld, y == firstYear);
            var toInYear = end < heldTo ? end : heldTo;
            var amount = toInYear >= heldTo
                ? yearAmount
                : Math.Round(yearAmount * (toInYear.DayNumber - from.DayNumber + 1) / daysHeld, 2, MidpointRounding.AwayFromZero);
            accumulated += amount;
            value -= amount;
        }
        return accumulated;
    }

    /// <summary>The written-down value at the end of a date.</summary>
    public static decimal WrittenDown(DepreciableAsset a, DateOnly to, YearStart? yearStart = null) => a.Cost - Accumulated(a, to, yearStart);

    /// <summary>A sale's gain (positive) or loss: the proceeds less the written-down value.</summary>
    public static decimal GainOnSale(decimal proceeds, decimal cost, decimal accumulated) => proceeds - (cost - accumulated);

    /// <summary>Problems with the method for this asset, or none. The thresholds are the small business ones.</summary>
    public static IReadOnlyList<string> Check(DepreciableAsset a)
    {
        var errors = new List<string>();
        if (!DepreciationMethods.All.Contains(a.Method))
            errors.Add("Choose a depreciation method.");
        if (DepreciationMethods.NeedsLife(a.Method) && a.LifeYears is not > 0)
            errors.Add("Enter the effective life in years.");
        if (a.LifeYears is > 100)
            errors.Add("The effective life can be at most 100 years.");
        if (a.Method == DepreciationMethods.LowValuePool && a.Cost - a.OpeningAccum >= LowValueThreshold && a.Cost >= LowValueThreshold)
            errors.Add($"The low-value pool is for assets costing less than {LowValueThreshold:C0}, or written down below it.");
        if (a.Method == DepreciationMethods.WriteOff && a.Cost >= WriteOffThreshold)
            errors.Add($"The instant asset write-off is for assets costing less than {WriteOffThreshold:C0}.");
        return errors;
    }
}
