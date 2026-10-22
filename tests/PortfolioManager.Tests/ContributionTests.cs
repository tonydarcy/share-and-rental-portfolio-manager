using PortfolioManager.Core.Performance;

namespace PortfolioManager.Tests;

public class ContributionTests
{
    private static readonly DateOnly From = new(2025, 7, 1), To = new(2026, 6, 30);

    private static PerfTrade Buy(DateOnly d, string code, double qty, decimal total, string ccy = "AUD", string ex = "ASX") =>
        new(d, ex, code, "Buy", qty, total, (double)total / qty, ccy, true, false);

    private static PerfTrade Sell(DateOnly d, string code, double qty, decimal total) =>
        new(d, "ASX", code, "Sell", qty, total, (double)total / qty, "AUD", true, false);

    private static PerfPrice P(string code, DateOnly d, double close, string ex = "ASX") => new(ex, code, d, close);

    private static ContributionReport Run(IEnumerable<PerfTrade> trades, IEnumerable<PerfPrice> prices, IEnumerable<PerfHoldingIncome>? income = null) =>
        ContributionCalculator.Calculate(new PerformanceInput("AUD", trades.ToList(), [], prices.ToList(), [], []), (income ?? []).ToList(), From, To);

    [Fact]
    public void Held_all_year_is_price_change_plus_income()
    {
        var r = Run([Buy(new(2024, 1, 1), "CBA", 100, 9000)],
            [P("CBA", new(2025, 6, 30), 100), P("CBA", new(2026, 6, 30), 120)],
            [new PerfHoldingIncome("ASX", "CBA", new(2025, 9, 1), 250)]);
        var h = Assert.Single(r.Holdings);
        Assert.Equal(10_000m, h.StartValue);
        Assert.Equal(12_000m, h.EndValue);
        Assert.Equal(2_000m, h.PriceGain);
        Assert.Equal(250m, h.Income);
        Assert.Equal(0m, h.CurrencyGain);
        Assert.Equal(2_250m, h.TotalGain);
        Assert.Equal(0.225, h.Return!.Value, 6);
    }

    [Fact]
    public void Buying_during_the_year_isnt_counted_as_gain()
    {
        var r = Run([Buy(new(2024, 1, 1), "BHP", 100, 4000), Buy(new(2026, 1, 1), "BHP", 100, 4500)],
            [P("BHP", new(2025, 6, 30), 40), P("BHP", new(2026, 6, 30), 50)]);
        var h = Assert.Single(r.Holdings);
        Assert.Equal(4_500m, h.NetBought);
        Assert.Equal(10_000m - 4_000m - 4_500m, h.PriceGain);
    }

    [Fact]
    public void Sold_during_the_year_counts_up_to_the_sale()
    {
        var r = Run([Buy(new(2024, 1, 1), "WES", 100, 5000), Sell(new(2025, 12, 1), "WES", 100, 6000)],
            [P("WES", new(2025, 6, 30), 50), P("WES", new(2026, 6, 30), 70)]);
        var h = Assert.Single(r.Holdings);
        Assert.Equal(0m, h.EndValue);
        Assert.Equal(-6_000m, h.NetBought);
        Assert.Equal(1_000m, h.PriceGain);
    }

    [Fact]
    public void Foreign_holding_splits_price_and_currency()
    {
        // 10 shares at US$100, AUD/USD style rates stored as USD/AUD (AUD per USD): 1.5 at the start, 1.6 at the end
        var r = Run([Buy(new(2024, 1, 1), "MSFT", 10, 1000, "USD", "NASDAQ")],
            [P("MSFT", new(2025, 6, 30), 100, "NASDAQ"), P("MSFT", new(2026, 6, 30), 110, "NASDAQ"),
             P("USD/AUD", new(2025, 6, 30), 1.5, "FOREX"), P("USD/AUD", new(2026, 6, 30), 1.6, "FOREX")]);
        var h = Assert.Single(r.Holdings);
        Assert.Equal(1_500m, h.StartValue);
        Assert.Equal(1_760m, h.EndValue);
        Assert.Equal(160m, h.PriceGain);     // US$100 gain at 1.6
        Assert.Equal(100m, h.CurrencyGain);  // US$1,000 held while the rate rose 0.1
        Assert.Equal(260m, h.TotalGain);
    }

    [Fact]
    public void Holdings_sold_before_the_period_are_left_out_and_shares_add_up()
    {
        var r = Run([Buy(new(2023, 1, 1), "OLD", 10, 100), Sell(new(2024, 1, 1), "OLD", 10, 120),
                     Buy(new(2024, 1, 1), "A", 100, 1000), Buy(new(2024, 1, 1), "B", 100, 1000)],
            [P("A", new(2025, 6, 30), 10), P("A", new(2026, 6, 30), 12), P("B", new(2025, 6, 30), 10), P("B", new(2026, 6, 30), 9)]);
        Assert.Equal(["A", "B"], r.Holdings.Select(h => h.Code));
        Assert.Equal(0.05, r.Holdings.Sum(h => h.ShareOfReturn!.Value), 6);
        Assert.Equal(0.05, r.Return!.Value, 6);
    }
}
