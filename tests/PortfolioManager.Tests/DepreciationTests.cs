using PortfolioManager.Core.Assets;

namespace PortfolioManager.Tests;

public class DepreciationTests
{
    private static readonly DateOnly July2025 = new(2025, 7, 1);

    private static FixedAsset Asset(decimal cost, string method, decimal? life, DateOnly start, decimal posted = 0) =>
        new(1, "FA-1", "Laptop", null, start, start, cost, method, life, null, 0, 10, 11, 12, null, null, null, null, null, posted, null);

    [Fact]
    public void Diminishing_value_is_double_the_prime_cost_rate_on_the_opening_value()
    {
        var a = new DepreciableAsset(10_000, DepreciationMethods.DiminishingValue, 5, July2025);
        Assert.Equal(4_000m, DepreciationEngine.Accumulated(a, new DateOnly(2026, 6, 30)));
        Assert.Equal(6_400m, DepreciationEngine.Accumulated(a, new DateOnly(2027, 6, 30)));
    }

    [Fact]
    public void Prime_cost_stops_at_the_cost()
    {
        var a = new DepreciableAsset(10_000, DepreciationMethods.PrimeCost, 4, July2025);
        Assert.Equal(2_500m, DepreciationEngine.Accumulated(a, new DateOnly(2026, 6, 30)));
        Assert.Equal(10_000m, DepreciationEngine.Accumulated(a, new DateOnly(2031, 6, 30)));
        Assert.Equal(0m, DepreciationEngine.WrittenDown(a, new DateOnly(2031, 6, 30)));
    }

    [Fact]
    public void First_year_counts_only_the_days_held()
    {
        // Held 1 January to 30 June: 181 days
        var a = new DepreciableAsset(10_000, DepreciationMethods.DiminishingValue, 5, new DateOnly(2026, 1, 1));
        Assert.Equal(1_983.56m, DepreciationEngine.Accumulated(a, new DateOnly(2026, 6, 30)));
    }

    [Fact]
    public void Part_of_a_year_is_pro_rated_by_days()
    {
        var a = new DepreciableAsset(10_000, DepreciationMethods.DiminishingValue, 5, July2025);
        Assert.Equal(2_016.44m, DepreciationEngine.Accumulated(a, new DateOnly(2025, 12, 31)));
    }

    [Fact]
    public void Low_value_pool_takes_the_first_year_rate_then_the_full_rate()
    {
        var a = new DepreciableAsset(900, DepreciationMethods.LowValuePool, null, new DateOnly(2025, 9, 1));
        Assert.Equal(168.75m, DepreciationEngine.Accumulated(a, new DateOnly(2026, 6, 30)));
        Assert.Equal(442.97m, DepreciationEngine.Accumulated(a, new DateOnly(2027, 6, 30)));
    }

    [Fact]
    public void Write_off_is_the_whole_cost_once_held()
    {
        var a = new DepreciableAsset(5_000, DepreciationMethods.WriteOff, null, new DateOnly(2026, 3, 1));
        Assert.Equal(0m, DepreciationEngine.Accumulated(a, new DateOnly(2026, 2, 28)));
        Assert.Equal(5_000m, DepreciationEngine.Accumulated(a, new DateOnly(2026, 3, 1)));
    }

    [Fact]
    public void Opening_depreciation_carries_on_from_its_date()
    {
        var a = new DepreciableAsset(10_000, DepreciationMethods.DiminishingValue, 5, new DateOnly(2023, 7, 1), new DateOnly(2025, 6, 30), 3_000);
        Assert.Equal(5_800m, DepreciationEngine.Accumulated(a, new DateOnly(2026, 6, 30)));
    }

    [Fact]
    public void Disposal_stops_depreciation_at_the_sale()
    {
        var a = new DepreciableAsset(10_000, DepreciationMethods.DiminishingValue, 5, July2025, Disposed: new DateOnly(2025, 12, 31));
        Assert.Equal(2_016.44m, DepreciationEngine.Accumulated(a, new DateOnly(2026, 6, 30)));
    }

    [Theory]
    [InlineData(DepreciationMethods.WriteOff, 20_000, null, false)]
    [InlineData(DepreciationMethods.WriteOff, 19_999, null, true)]
    [InlineData(DepreciationMethods.LowValuePool, 1_500, null, false)]
    [InlineData(DepreciationMethods.LowValuePool, 999, null, true)]
    [InlineData(DepreciationMethods.DiminishingValue, 5_000, null, false)]
    [InlineData(DepreciationMethods.DiminishingValue, 5_000, 4.0, true)]
    public void Check_applies_the_method_rules(string method, double cost, double? life, bool ok)
    {
        var errors = DepreciationEngine.Check(new DepreciableAsset((decimal)cost, method, (decimal?)life, July2025));
        Assert.Equal(ok, errors.Count == 0);
    }

    [Fact]
    public void A_run_posts_what_is_not_yet_posted()
    {
        var lines = FixedAssetRules.Run([Asset(10_000, DepreciationMethods.DiminishingValue, 5, July2025, posted: 1_000)], new DateOnly(2026, 6, 30));
        var line = Assert.Single(lines);
        Assert.Equal(4_000m, line.ToDate);
        Assert.Equal(3_000m, line.Amount);
        Assert.Equal("<assets><a id=\"1\" v=\"3000\" /></assets>", FixedAssetRules.RunXml(lines));
    }

    [Fact]
    public void A_run_leaves_out_assets_that_are_fully_posted()
    {
        Assert.Empty(FixedAssetRules.Run([Asset(10_000, DepreciationMethods.PrimeCost, 4, July2025, posted: 2_500)], new DateOnly(2026, 6, 30)));
    }

    [Fact]
    public void Disposal_preview_gives_the_final_depreciation_and_the_gain()
    {
        var preview = FixedAssetRules.Disposal(Asset(10_000, DepreciationMethods.DiminishingValue, 5, July2025), new DateOnly(2025, 12, 31), 9_000);
        Assert.Equal(2_016.44m, preview.FinalDepreciation);
        Assert.Equal(7_983.56m, preview.WrittenDown);
        Assert.Equal(1_016.44m, preview.Gain);
    }
}
