using PortfolioManager.Core.Tax;

namespace PortfolioManager.Tests;

public class CapitalGainsSalesTests
{
    private static readonly DateOnly Sold = new(2026, 3, 15);

    [Fact]
    public void One_line_per_sale_with_its_parcels_added_together_sorted_by_date()
    {
        CgtEvent[] events =
        [
            new("ASX", "CBA", new DateOnly(2020, 1, 1), Sold, 60, 600m, 900m, SellTradeId: 9, BuyTradeId: 1),
            new("ASX", "CBA", new DateOnly(2024, 1, 1), Sold, 40, 800m, 600m, SellTradeId: 9, BuyTradeId: 2),
            new("ASX", "BHP", new DateOnly(2020, 1, 1), new DateOnly(2025, 8, 2), 10, 500m, 450m, SellTradeId: 7, BuyTradeId: 3),
            new("ASX", "XYZ", new DateOnly(2020, 1, 1), Sold, 10, 0m, 50m, CgtEventType.CapitalReturnExcess),
        ];
        var report = CapitalGainsService.FromEvents(events);

        Assert.Equal(["BHP", "CBA"], report.Lines.Select(l => l.StockCode));
        var cba = report.Lines[1];
        Assert.Equal((1400m, 1500m, 100m, 14m, 15m, 1m), (cba.TotalCost, cba.TotalProceeds, cba.Gain, cba.CostPerUnit, cba.ProceedsPerUnit, cba.GainPerUnit));
        Assert.Equal(Sold, cba.DateSold);
        Assert.Equal((1900m, 1950m, 50m), (report.Total.TotalCost, report.Total.TotalProceeds, report.Total.Gain));
    }

    [Fact]
    public void A_chosen_parcel_shows_in_the_sales_table()
    {
        TradeLot[] trades =
        [
            new(1, "ASX", "CBA", "Buy", new DateOnly(2020, 1, 1), 100, 10m),
            new(2, "ASX", "CBA", "Buy", new DateOnly(2024, 1, 1), 100, 30m),
            new(3, "ASX", "CBA", "Sell", Sold, 100, 25m),
        ];
        var fifo = CapitalGainsService.FromEvents(CgtParcels.Match(trades).Events);
        var picked = CapitalGainsService.FromEvents(CgtParcels.Match(trades, null, null,
            new Dictionary<int, SaleParcelChoice> { [3] = new(3, ParcelMethod.Specific, [new ParcelPick(2, 100)]) }).Events);

        Assert.Equal(1500m, fifo.Total.Gain);
        Assert.Equal(-500m, picked.Total.Gain);
    }

    [Fact]
    public void No_sales_is_an_empty_report()
    {
        var report = CapitalGainsService.FromEvents([]);
        Assert.Empty(report.Lines);
        Assert.Equal(0m, report.Total.Gain);
    }
}

public class UnrealisedGainsMapTests
{
    private static Dictionary<string, object?> Line(string code, object? price, double value, double cost) => new()
    {
        ["Stock Exchange"] = "ASX",
        ["Stock Code"] = code,
        ["Current Holding"] = 100.0,
        ["Cost Basis Per Unit"] = cost / 100,
        ["Total Cost Basis"] = cost,
        ["Market Price"] = price,
        ["Market Value"] = value,
        ["Total Gain / Loss"] = value - cost,
    };

    private static Dictionary<string, object?> Total(object? cost, object? value, object? gain) => new()
    {
        ["Stock Exchange"] = "Total:",
        ["Stock Code"] = DBNull.Value,
        ["Current Holding"] = DBNull.Value,
        ["Cost Basis Per Unit"] = DBNull.Value,
        ["Total Cost Basis"] = cost,
        ["Market Price"] = DBNull.Value,
        ["Market Value"] = value,
        ["Total Gain / Loss"] = gain,
    };

    [Fact]
    public void Takes_out_the_total_row_wherever_it_is_and_sorts_by_code()
    {
        var report = UnrealisedGainsService.Map([Total(1500.0, 1800.0, 300.0), Line("WES", 12.0, 1200, 1000), Line("BHP", 6.0, 600, 500)]);

        Assert.Equal(["BHP", "WES"], report.Lines.Select(l => l.StockCode));
        Assert.Equal(1800m, report.Total.MarketValue);
        Assert.Equal(300m, report.Total.Gain);
        Assert.False(report.AnyMissingPrices);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0.0)]
    public void A_null_or_zero_price_is_missing(double? price)
    {
        var report = UnrealisedGainsService.Map([Line("XYZ", price is null ? DBNull.Value : (object)price, 0, 500)]);
        Assert.Null(report.Lines[0].MarketPrice);
        Assert.Equal(-500m, report.Lines[0].Gain);
        Assert.True(report.AnyMissingPrices);
    }

    [Fact]
    public void Builds_lines_from_the_open_parcels_grouped_or_not()
    {
        OpenParcel[] open =
        [
            new("ASX", "CBA", new DateOnly(2020, 1, 1), 60, 10m),
            new("ASX", "CBA", new DateOnly(2024, 1, 1), 40, 30m),
            new("NASDAQ", "AAPL", new DateOnly(2024, 1, 1), 0.5m, 300m),
        ];
        var prices = new Dictionary<string, decimal> { ["ASX:CBA"] = 20m };

        var byParcel = UnrealisedGainsService.Build(open, prices, groupByStock: false);
        Assert.Equal(["AAPL", "CBA", "CBA"], byParcel.Lines.Select(l => l.StockCode));
        Assert.Equal([600m, 1200m], byParcel.Lines.Where(l => l.StockCode == "CBA").Select(l => l.TotalCost));
        Assert.True(byParcel.AnyMissingPrices);
        Assert.Equal(-150m, byParcel.Lines[0].Gain); // no price: valued at nil

        var grouped = UnrealisedGainsService.Build(open, prices, groupByStock: true);
        var cba = grouped.Lines.Single(l => l.StockCode == "CBA");
        Assert.Equal((100m, 18m, 1800m, 2000m, 200m), (cba.Units, cba.CostPerUnit, cba.TotalCost, cba.MarketValue, cba.Gain));
        Assert.Equal(1950m, grouped.Total.TotalCost);
    }

    [Fact]
    public void An_all_null_total_with_no_parcels_is_zero()
    {
        var report = UnrealisedGainsService.Map([Total(DBNull.Value, DBNull.Value, DBNull.Value)]);
        Assert.Empty(report.Lines);
        Assert.Equal(0m, report.Total.MarketValue);
    }
}

public class TaxableIncomeMapTests
{
    [Fact]
    public void Null_sums_with_no_payouts_show_as_zero()
    {
        var summary = new Dictionary<string, object?>
        {
            ["ReportSection"] = "Summary",
            ["TotalUnfranked"] = DBNull.Value,
            ["TotalFranked"] = DBNull.Value,
            ["TotalFrankingCredits"] = DBNull.Value,
            ["TotalInterest"] = DBNull.Value,
            ["TotalForeignTaxPaid"] = DBNull.Value,
            ["TotalTrustIncome"] = DBNull.Value,
            ["GrossTaxableIncome"] = DBNull.Value,
            ["TaxWithheld"] = DBNull.Value,
        };

        var report = TaxableIncomeService.Map(summary, []);

        Assert.Equal(new TaxableIncomeSummary(0, 0, 0, 0, 0, 0, 0, 0), report.Summary);
        Assert.Empty(report.Lines);
    }

    [Fact]
    public void A_missing_summary_row_shows_as_zero() =>
        Assert.Equal(0m, TaxableIncomeService.Map(null, []).Summary.GrossIncome);

    [Fact]
    public void Maps_the_summary_and_breakdown()
    {
        var summary = new Dictionary<string, object?>
        {
            ["TotalUnfranked"] = 10.0,
            ["TotalFranked"] = 70.0,
            ["TotalFrankingCredits"] = 30.0,
            ["TotalInterest"] = 5.0,
            ["TotalForeignTaxPaid"] = 1.5,
            ["TotalTrustIncome"] = 2.0,
            ["GrossTaxableIncome"] = 117.0,
            ["TaxWithheld"] = 0.0,
        };
        var line = new Dictionary<string, object?>
        {
            ["Exchange"] = "ASX",
            ["Code"] = "CBA ",
            ["PaymentCount"] = 2,
            ["CashReceived"] = 87.0,
            ["Unfranked"] = 10.0,
            ["Franked"] = 70.0,
            ["FrankingCredits"] = 30.0,
            ["Interest"] = 5.0,
            ["TrustIncome"] = 2.0,
            ["ForeignTaxPaid"] = DBNull.Value,
            ["GrossIncome"] = 117.0,
        };

        var report = TaxableIncomeService.Map(summary, [line]);

        Assert.Equal(new TaxableIncomeSummary(10, 70, 30, 5, 2, 1.5m, 0, 117), report.Summary);
        var l = Assert.Single(report.Lines);
        Assert.Equal("CBA", l.StockCode);
        Assert.Equal(2, l.PaymentCount);
        Assert.Equal(87m, l.CashReceived);
        Assert.Equal(0m, l.ForeignTaxPaid);
    }
}
