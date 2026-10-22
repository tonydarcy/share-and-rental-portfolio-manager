using PortfolioManager.Core.TaxWork;

namespace PortfolioManager.Tests;

public class RentalTests
{
    private static RentalAsset Plant(decimal cost, decimal life, string method, DateOnly start) =>
        new(1, 1, RentalAssetKinds.Plant, "Oven", start, cost, life, method, null, null);

    [Fact]
    public void Diminishing_value_first_year_is_pro_rata_by_days()
    {
        // Held 273 days of 2025-26: 1,200 x 273/365 x 200%/12 = 149.59
        var a = RentalDepreciation.For(Plant(1200, 12, RentalAssetKinds.DiminishingValue, new DateOnly(2025, 10, 1)), 2026);
        Assert.Equal(273, a.Days);
        Assert.Equal(149.59m, a.Deduction);
        Assert.Equal(1050.41m, a.Closing);
    }

    [Fact]
    public void Diminishing_value_later_years_work_from_the_opening_value()
    {
        var a = RentalDepreciation.For(Plant(1200, 12, RentalAssetKinds.DiminishingValue, new DateOnly(2025, 10, 1)), 2027);
        Assert.Equal(1050.41m, a.Opening);
        Assert.Equal(365, a.Days);
        Assert.Equal(175.07m, a.Deduction);
    }

    [Fact]
    public void Prime_cost_stops_at_nil()
    {
        var asset = Plant(1000, 2, RentalAssetKinds.PrimeCost, new DateOnly(2024, 7, 1));
        Assert.Equal(500m, RentalDepreciation.For(asset, 2025).Deduction);
        Assert.Equal(500m, RentalDepreciation.For(asset, 2026).Deduction);
        Assert.Equal(0m, RentalDepreciation.For(asset, 2027).Deduction);
        Assert.Equal(0m, RentalDepreciation.For(asset, 2024).Opening);
    }

    [Fact]
    public void Capital_works_at_two_and_a_half_percent()
    {
        var building = new RentalAsset(2, 1, RentalAssetKinds.Building, "Building", new DateOnly(2020, 1, 1), 400_000, null, null, 2.5m, null);
        Assert.Equal(10_000m, RentalDepreciation.For(building, 2026).Deduction);
    }

    [Fact]
    public void Disposal_ends_the_days_held()
    {
        var a = Plant(1000, 10, RentalAssetKinds.PrimeCost, new DateOnly(2024, 7, 1)) with { Disposed = new DateOnly(2025, 7, 31) };
        Assert.Equal(31, RentalDepreciation.For(a, 2026).Days);
        Assert.Equal(0, RentalDepreciation.For(a, 2027).Days);
    }

    [Fact]
    public void Schedule_takes_the_owners_share_into_the_labels()
    {
        var property = new RentalProperty(1, "1 Test St", null, 50, true, null);
        var amounts = new Dictionary<string, decimal> { ["GrossRent"] = 20_000, ["OtherIncome"] = 400, ["Interest"] = 12_000, ["Insurance"] = 1_000, ["Weeks"] = 52 };
        var building = new RentalAsset(2, 1, RentalAssetKinds.Building, "Building", new DateOnly(2020, 1, 1), 400_000, null, null, 2.5m, null);
        var plant = Plant(1200, 12, RentalAssetKinds.DiminishingValue, new DateOnly(2025, 10, 1));
        var year = new RentalPropertyYear(property, 2026, amounts, [RentalDepreciation.For(building, 2026), RentalDepreciation.For(plant, 2026)]);
        var s = new RentalSchedule(2026, [year]);
        Assert.Equal(10_200m, s.GrossRent);
        Assert.Equal(6_000m, s.Interest);
        Assert.Equal(5_000m, s.CapitalWorks);
        Assert.Equal(574.80m, s.OtherDeductions);
        Assert.Equal(-1_374.80m, s.Net);
        Assert.Equal(52m, year.Weeks);
    }

    [Fact]
    public void Amounts_xml_leaves_out_nil_amounts()
    {
        var xml = RentalAmountsXml.Write(new Dictionary<string, decimal> { ["GrossRent"] = 1234.5m, ["Water"] = 0 });
        Assert.Equal("<a c=\"GrossRent\" v=\"1234.5\" />", xml);
    }
}
