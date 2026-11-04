namespace PortfolioManager.Core.Tax;

/// <summary>One extra line under the capital gains summary for a country's own allowance or inclusion rate.</summary>
public sealed record CgtAllowanceLine(string Label, decimal Amount, string ResultLabel, decimal Result, string Note);

/// <summary>A capital loss where the same share was also bought within the window either side of the sale.</summary>
public sealed record RepurchaseWarning(string ExchangeCode, string StockCode, DateOnly Sold, decimal Loss, DateOnly Bought, decimal UnitsBought);

/// <summary>
/// Simple country rules for capital gains outside Australia: the UK annual exempt amount, Canada's inclusion rate,
/// India's long-term exemption, and the US wash sale / Canadian superficial loss check (a warning only, nothing is
/// recalculated). Rates are for the 2025-26 tax years and await Tony's check.
/// </summary>
public static class CountryCgt
{
    public const decimal UkAnnualExemptAmount = 3_000m;
    public const decimal UkTrustExemptAmount = 1_500m;
    public const decimal CanadaInclusionRate = 0.5m;
    public const decimal IndiaLongTermExemption = 125_000m;

    /// <summary>Days either side of a loss sale that a repurchase makes the loss suspect (US and Canada: 30).</summary>
    public const int RepurchaseWindowDays = 30;

    /// <summary>The countries whose rules deny a loss when the same share is bought back within 30 days.</summary>
    public static string? RepurchaseRuleName(string countryCode) => countryCode switch
    {
        "US" => "wash sale",
        "CA" => "superficial loss",
        _ => null,
    };

    /// <summary>The line to add for the country, or null when it has none (or doesn't apply to this entity type).</summary>
    public static CgtAllowanceLine? Allowance(string countryCode, string? entityType, CgtSummary summary)
    {
        var t = (entityType ?? "").ToLowerInvariant();
        var net = summary.NetCapitalGain;
        switch (countryCode)
        {
            case "GB":
            {
                // Companies pay corporation tax on gains and get no exempt amount
                if (t.Contains("company") || t.Contains("super"))
                    return null;
                var allowance = t.Contains("trust") ? UkTrustExemptAmount : UkAnnualExemptAmount;
                var used = Math.Min(allowance, Math.Max(0m, net));
                return new("Annual exempt amount", -used, "Gains above the exempt amount", net - used,
                    $"Individuals get £{UkAnnualExemptAmount:#,0} a tax year (most trusts £{UkTrustExemptAmount:#,0}). Losses are only set against gains above it in practice; check before using these figures.");
            }
            case "CA":
            {
                var taxable = Math.Round(Math.Max(0m, net) * CanadaInclusionRate, 2, MidpointRounding.AwayFromZero);
                return new("Not included (50% inclusion rate)", taxable - Math.Max(0m, net), "Taxable capital gain", taxable,
                    "Half of a net capital gain is taxable in Canada. Gains here use first in, first out cost, not the adjusted cost base (average cost) Canada requires.");
            }
            case "IN":
            {
                if (t.Contains("company"))
                    return null;
                // Long-term gains left after losses: the gains on parcels held more than 12 months
                var longTerm = Math.Max(0m, summary.DiscountGains - summary.LossesAppliedToDiscountGains);
                var used = Math.Min(IndiaLongTermExemption, longTerm);
                return new("Long-term exemption (listed shares and equity funds)", -used, "Gains after the exemption", net - used,
                    "Long-term gains on listed shares and equity funds (held more than 12 months) are exempt up to ₹1.25 lakh a year. Other assets use 24 months, which isn't worked out here.");
            }
            default:
                return null;
        }
    }

    /// <summary>
    /// Losses where the same share (exchange and code) was bought within <see cref="RepurchaseWindowDays"/> days
    /// before or after the sale. The parcel that was sold doesn't count as a repurchase.
    /// </summary>
    public static IReadOnlyList<RepurchaseWarning> Repurchases(IEnumerable<CgtEvent> events, IEnumerable<TradeLot> trades)
    {
        var buys = trades.Where(t => t.Type.Trim().Equals("BUY", StringComparison.OrdinalIgnoreCase))
            .ToLookup(t => (Key(t.ExchangeCode), Key(t.StockCode)));
        var warnings = new List<RepurchaseWarning>();
        foreach (var e in events.Where(e => e.Gain < 0 && e.Type == CgtEventType.Sale))
        {
            var match = buys[(Key(e.ExchangeCode), Key(e.StockCode))]
                .Where(b => b.TradeId != e.BuyTradeId && Math.Abs(b.Date.DayNumber - e.Disposed.DayNumber) <= RepurchaseWindowDays)
                .OrderBy(b => Math.Abs(b.Date.DayNumber - e.Disposed.DayNumber))
                .FirstOrDefault();
            if (match is not null)
                warnings.Add(new(e.ExchangeCode, e.StockCode, e.Disposed, -e.Gain, match.Date, match.Quantity));
        }
        // One line per sale and buy, even when the sale used several parcels
        return warnings
            .GroupBy(w => (w.ExchangeCode, w.StockCode, w.Sold, w.Bought))
            .Select(g => g.First() with { Loss = g.Sum(w => w.Loss) })
            .OrderBy(w => w.Sold).ThenBy(w => w.StockCode)
            .ToList();
    }

    private static string Key(string s) => s.Trim().ToUpperInvariant();
}
