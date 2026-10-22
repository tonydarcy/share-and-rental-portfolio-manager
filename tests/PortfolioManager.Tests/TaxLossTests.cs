using PortfolioManager.Core.Tax;

namespace PortfolioManager.Tests;

public class TaxLossTests
{
    private static readonly CgtDiscountRate Individual = new(CgtEntityKind.Individual, 0.5m, false);
    private static readonly DateOnly Today = new(2026, 10, 1);

    private static readonly TradeLot[] Trades =
    [
        new(1, "ASX", "CBA", "Buy", new DateOnly(2020, 1, 1), 100, 50m),
        new(2, "ASX", "CBA", "Buy", new DateOnly(2026, 6, 1), 100, 120m),
        // A $1,000 gain this year on a parcel held under a year
        new(3, "ASX", "BHP", "Buy", new DateOnly(2026, 1, 1), 100, 40m),
        new(4, "ASX", "BHP", "Sell", new DateOnly(2026, 8, 1), 100, 50m),
        new(5, "ASX", "WES", "Buy", new DateOnly(2026, 9, 20), 10, 80m),
        new(6, "ASX", "XRO", "Buy", new DateOnly(2025, 1, 1), 10, 100m),
    ];

    private static TaxLossReport Find(params ((string, string) Key, decimal Price)[] prices) =>
        TaxLoss.Find(Trades, new Dictionary<int, SaleParcelChoice>(), [],
            prices.ToDictionary(p => p.Key, p => (p.Price, Today)), Individual, 0.32m, Today);

    [Fact]
    public void Only_parcels_below_cost_are_sold()
    {
        var r = Find((("ASX", "CBA"), 100m), (("ASX", "WES"), 90m), (("ASX", "XRO"), 150m));
        var c = Assert.Single(r.Candidates);
        Assert.Equal(("CBA", 200m, 100m), (c.StockCode, c.UnitsHeld, c.UnitsAtLoss));
        Assert.Equal(-2000m, c.UnrealisedLoss);
        Assert.Equal(1000m, r.SoFar.NetCapitalGain);
        // The $2,000 loss wipes out the $1,000 gain and $1,000 carries forward
        Assert.Equal(-1000m, c.Sale.NetGainChange);
        Assert.Equal(320m, c.TaxSaved);
        Assert.Equal(1000m, c.Sale.LossCarriedForwardChange);
    }

    [Fact]
    public void Recent_purchases_are_flagged_and_missing_prices_listed()
    {
        var r = Find((("ASX", "WES"), 70m));
        var c = Assert.Single(r.Candidates);
        Assert.True(c.BoughtRecently(Today));
        Assert.Equal(["CBA", "XRO"], r.NoPrice);
    }

    [Fact]
    public void Nothing_at_a_loss_still_shows_this_years_gains()
    {
        var r = Find((("ASX", "XRO"), 150m));
        Assert.Empty(r.Candidates);
        Assert.Equal(1000m, r.SoFar.NetCapitalGain);
        Assert.Equal(2027, r.Year.FinancialYear);
    }

    [Fact]
    public void Loss_parcels_total_their_shortfall()
    {
        var (units, loss) = TaxLoss.LossParcels(
            [new OpenParcel("ASX", "A", new(2020, 1, 1), 10, 5m), new OpenParcel("ASX", "A", new(2021, 1, 1), 5, 3m)], 4m);
        Assert.Equal((10m, -10m), (units, loss));
    }
}
