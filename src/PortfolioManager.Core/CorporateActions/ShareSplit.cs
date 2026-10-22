using PortfolioManager.Core.Tax;
using PortfolioManager.Core.Validation;

namespace PortfolioManager.Core.CorporateActions;

/// <summary>NewUnits for every OldUnits held: 2 for 1 is a split, 1 for 10 a consolidation.</summary>
public sealed record SplitRatio(int NewUnits, int OldUnits)
{
    /// <summary>New units per old unit, as tblCorporateActions.dblRatio stores it.</summary>
    public decimal Factor => (decimal)NewUnits / OldUnits;

    public bool IsConsolidation => NewUnits < OldUnits;

    public string Kind => IsConsolidation ? "Consolidation" : "Split";

    public override string ToString() => $"{NewUnits:N0} for {OldUnits:N0}";

    /// <summary>The smallest whole-number ratio for a stored factor (e.g. 0.1 is 1 for 10), or null if there's none up to 1,000.</summary>
    public static SplitRatio? FromFactor(double factor)
    {
        if (!(factor > 0) || double.IsInfinity(factor))
            return null;
        for (var old = 1; old <= 1000; old++)
        {
            var units = factor * old;
            var whole = Math.Round(units);
            if (whole >= 1 && whole <= int.MaxValue && Math.Abs(units - whole) < 1e-6 * Math.Max(1, units))
                return new SplitRatio((int)whole, old);
        }
        return null;
    }
}

/// <summary>What the record-a-split form posts.</summary>
public sealed class SplitForm
{
    public string? Market { get; set; }
    public string? Code { get; set; }

    /// <summary>The first day the shares trade on the new basis (the ex-date).</summary>
    public DateOnly? Date { get; set; }

    public int? NewUnits { get; set; }
    public int? OldUnits { get; set; }
}

public sealed record NewSplit(string Market, string Code, DateOnly Date, SplitRatio Ratio);

/// <summary>The holding the day before a split and the same holding in the new units.</summary>
public sealed record SplitPreview(int TradesAdjusted, decimal UnitsBefore, decimal UnitsAfter, decimal CostBaseBefore, decimal CostBaseAfter)
{
    /// <summary>A consolidation can leave part of a unit, which the registry will have rounded.</summary>
    public bool HasFractionalUnits => UnitsAfter != decimal.Truncate(UnitsAfter);
}

/// <summary>
/// Share splits and consolidations, worked the way uspApplyStockSplit stores them: every trade before
/// the effective date gets units times the ratio and price and cost per unit divided by it. Order
/// totals don't change, so the cost base is the same, and purchase dates (for the CGT discount) are kept.
/// </summary>
public static class ShareSplit
{
    public const int MaxUnits = 1_000_000;

    public static TradeLot Adjust(TradeLot trade, SplitRatio ratio) => trade with
    {
        Quantity = trade.Quantity * ratio.NewUnits / ratio.OldUnits,
        CostBasisPerUnit = trade.CostBasisPerUnit * ratio.OldUnits / ratio.NewUnits,
    };

    /// <summary>The trades as they'll be after the split: the holding's trades before the date adjusted, all others as they were.</summary>
    public static IReadOnlyList<TradeLot> Apply(IEnumerable<TradeLot> trades, string market, string code, DateOnly effective, SplitRatio ratio) =>
        trades.Select(t => IsBefore(t, market, code, effective) ? Adjust(t, ratio) : t).ToList();

    public static SplitPreview Preview(IEnumerable<TradeLot> trades, string market, string code, DateOnly effective, SplitRatio ratio)
    {
        var holding = trades.Where(t => Same(t, market, code)).ToList();
        var dayBefore = effective.AddDays(-1);
        var before = CgtParcels.Match(holding, dayBefore).Open;
        var after = CgtParcels.Match(Apply(holding, market, code, effective, ratio), dayBefore).Open;
        return new SplitPreview(
            holding.Count(t => t.Date < effective),
            before.Sum(p => p.Units), after.Sum(p => p.Units),
            before.Sum(p => p.TotalCost), after.Sum(p => p.TotalCost));
    }

    public static Validated<NewSplit> Validate(SplitForm f, DateOnly today)
    {
        var errors = new List<FieldError>();

        var market = FormRules.Clean(f.Market)?.ToUpperInvariant();
        if (market is null)
            errors.Add(new(nameof(f.Market), "Enter the stock exchange, e.g. ASX."));
        FormRules.MaxLength(errors, nameof(f.Market), "Stock exchange", market, 50);

        var code = FormRules.Clean(f.Code)?.ToUpperInvariant();
        if (code is null)
            errors.Add(new(nameof(f.Code), "Enter the stock code."));
        FormRules.MaxLength(errors, nameof(f.Code), "Stock code", code, 50);

        if (f.Date is not { } date)
            errors.Add(new(nameof(f.Date), "Enter the date the shares started trading on the new basis."));
        else if (date > today)
            errors.Add(new(nameof(f.Date), "Record the split once it has happened; the date can't be in the future."));
        else if (date < FormRules.EarliestDate)
            errors.Add(new(nameof(f.Date), "Check the date."));

        if (f.NewUnits is not { } n || n < 1 || n > MaxUnits)
            errors.Add(new(nameof(f.NewUnits), $"Enter the new units as a whole number from 1 to {MaxUnits:N0}."));
        if (f.OldUnits is not { } o || o < 1 || o > MaxUnits)
            errors.Add(new(nameof(f.OldUnits), $"Enter the old units as a whole number from 1 to {MaxUnits:N0}."));
        else if (f.NewUnits == f.OldUnits)
            errors.Add(new(nameof(f.NewUnits), "One for one doesn't change anything. For a 2 for 1 split enter 2 and 1; for a 1 for 10 consolidation enter 1 and 10."));

        if (errors.Count > 0)
            return new(null, errors);

        var gcd = Gcd(f.NewUnits!.Value, f.OldUnits!.Value);
        return new(new NewSplit(market!, code!, f.Date!.Value, new SplitRatio(f.NewUnits.Value / gcd, f.OldUnits.Value / gcd)), errors);
    }

    private static bool Same(TradeLot t, string market, string code) =>
        t.ExchangeCode.Trim().Equals(market.Trim(), StringComparison.OrdinalIgnoreCase)
        && t.StockCode.Trim().Equals(code.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool IsBefore(TradeLot t, string market, string code, DateOnly effective) =>
        t.Date < effective && Same(t, market, code);

    private static int Gcd(int a, int b) => b == 0 ? a : Gcd(b, a % b);
}
