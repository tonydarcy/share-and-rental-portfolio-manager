using PortfolioManager.Core.Tax;
using PortfolioManager.Core.Validation;

namespace PortfolioManager.Core.CorporateActions;

/// <summary>What the record-a-return-of-capital form posts: an amount per unit or a total, not both.</summary>
public sealed class CapitalReturnForm
{
    public string? Market { get; set; }
    public string? Code { get; set; }
    public DateOnly? Date { get; set; }
    public decimal? PerUnit { get; set; }
    public decimal? Total { get; set; }
    public string? Comment { get; set; }
}

public sealed record NewCapitalReturn(string Market, string Code, DateOnly Date, decimal? PerUnit, decimal? Total, string? Comment);

/// <summary>One parcel held on the day and its part of the return.</summary>
public sealed record CapitalReturnParcel(DateOnly Acquired, CapitalReturnShare Share);

/// <summary>
/// What a return of capital will do. UnitsHeld is in today's units (after any later splits);
/// UnitsOnTheDay is what was actually held then, which an amount per unit is paid on.
/// </summary>
public sealed record CapitalReturnPreview(decimal UnitsHeld, decimal UnitsOnTheDay, decimal Total, IReadOnlyList<CapitalReturnParcel> Parcels)
{
    public decimal CostBaseBefore => Parcels.Sum(p => p.Share.CostBaseBefore);
    public decimal Reduction => Parcels.Sum(p => p.Share.Reduction);

    /// <summary>The capital gain (CGT event G1) where the return is more than a parcel's cost base.</summary>
    public decimal Excess => Parcels.Sum(p => p.Share.Excess);
}

public static class ReturnOfCapitalEntry
{
    public static Validated<NewCapitalReturn> Validate(CapitalReturnForm f, DateOnly today)
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
            errors.Add(new(nameof(f.Date), "Enter the payment date."));
        else if (date > today)
            errors.Add(new(nameof(f.Date), "The payment date can't be in the future."));
        else if (date < FormRules.EarliestDate)
            errors.Add(new(nameof(f.Date), "Check the payment date."));

        if (f.PerUnit is null && f.Total is null)
            errors.Add(new(nameof(f.PerUnit), "Enter the amount per unit or the total received."));
        else if (f.PerUnit is not null && f.Total is not null)
            errors.Add(new(nameof(f.PerUnit), "Enter the amount per unit or the total received, not both."));
        else if (f.PerUnit is { } perUnit && perUnit <= 0)
            errors.Add(new(nameof(f.PerUnit), "The amount per unit must be more than zero."));
        else if (f.Total is { } total)
        {
            if (total <= 0)
                errors.Add(new(nameof(f.Total), "The total must be more than zero."));
            else if (FormRules.HasMoreThanCents(total))
                errors.Add(new(nameof(f.Total), "Enter the total in dollars and cents."));
            else if (total > FormRules.MaxMoney)
                errors.Add(new(nameof(f.Total), "That amount is too large to record."));
        }

        var comment = FormRules.Clean(f.Comment);
        FormRules.MaxLength(errors, nameof(f.Comment), "Comment", comment, 500);

        if (errors.Count > 0)
            return new(null, errors);
        return new(new NewCapitalReturn(market!, code!, f.Date!.Value, f.PerUnit, f.Total, comment), errors);
    }

    /// <summary>
    /// Spreads the return over the parcels held at the end of the payment date, after any earlier
    /// returns of capital. laterSplitFactors are the holding's recorded splits after that date.
    /// </summary>
    public static CapitalReturnPreview Preview(
        IEnumerable<TradeLot> trades, IEnumerable<CapitalReturn> earlierReturns, NewCapitalReturn r, IEnumerable<decimal> laterSplitFactors)
    {
        var holding = trades.Where(t => Same(t.ExchangeCode, t.StockCode, r)).ToList();
        var returns = earlierReturns.Where(c => Same(c.ExchangeCode, c.StockCode, r)).ToList();
        var parcels = CgtParcels.Match(holding, r.Date, returns).Open;

        var units = parcels.Sum(p => p.Units);
        var onTheDay = CapitalReturns.UnitsOnTheDay(units, laterSplitFactors);
        var total = r.Total ?? CapitalReturns.TotalFromPerUnit(r.PerUnit ?? 0m, onTheDay);
        var shares = CapitalReturns.Apportion(parcels.Select(p => (p.Units, p.Units * p.CostPerUnit)).ToList(), total);

        return new CapitalReturnPreview(units, onTheDay, total,
            parcels.Zip(shares, (p, s) => new CapitalReturnParcel(p.Acquired, s)).ToList());
    }

    private static bool Same(string exchange, string code, NewCapitalReturn r) =>
        exchange.Trim().Equals(r.Market, StringComparison.OrdinalIgnoreCase)
        && code.Trim().Equals(r.Code, StringComparison.OrdinalIgnoreCase);
}
