using PortfolioManager.Core.Tax;

namespace PortfolioManager.Tests;

public class CapitalGainsMapTests
{
    private static Dictionary<string, object?> Sale(string item, string date, double cost, double proceeds) => new()
    {
        ["Stock Exchange & Stock Code"] = item,
        ["Date Sold"] = date,
        ["Cost Basis Per Unit"] = cost / 100,
        ["Total Cost Basis"] = cost,
        ["Proceeds Per Unit"] = proceeds / 100,
        ["Total Proceeds"] = proceeds,
        ["Gain / Loss Per Unit"] = (proceeds - cost) / 100,
        ["Total Gain / Loss"] = proceeds - cost,
    };

    private static Dictionary<string, object?> Total(object? cost, object? proceeds, object? gain) => new()
    {
        ["Stock Exchange & Stock Code"] = "Total:",
        ["Date Sold"] = DBNull.Value,
        ["Cost Basis Per Unit"] = DBNull.Value,
        ["Total Cost Basis"] = cost,
        ["Proceeds Per Unit"] = DBNull.Value,
        ["Total Proceeds"] = proceeds,
        ["Gain / Loss Per Unit"] = DBNull.Value,
        ["Total Gain / Loss"] = gain,
    };

    [Fact]
    public void Takes_out_the_total_row_wherever_it_is_and_sorts_sales_by_date()
    {
        var report = CapitalGainsService.Map([
            Sale("ASX:CBA", "15 Mar 2026", 1000, 1200),
            Total(1500.0, 1650.0, 150.0),
            Sale("ASX:BHP", "02 Aug 2025", 500, 450),
        ]);

        Assert.Equal(["BHP", "CBA"], report.Lines.Select(l => l.StockCode));
        Assert.Equal("ASX", report.Lines[0].ExchangeCode);
        Assert.Equal(new DateOnly(2025, 8, 2), report.Lines[0].DateSold);
        Assert.Equal(-50m, report.Lines[0].Gain);
        Assert.Equal(1500m, report.Total.TotalCost);
        Assert.Equal(1650m, report.Total.TotalProceeds);
        Assert.Equal(150m, report.Total.Gain);
    }

    [Fact]
    public void An_all_null_total_with_no_sales_is_zero()
    {
        var report = CapitalGainsService.Map([Total(DBNull.Value, DBNull.Value, DBNull.Value)]);
        Assert.Empty(report.Lines);
        Assert.Equal(0m, report.Total.Gain);
        Assert.Equal(0m, report.Total.TotalCost);
    }

    [Fact]
    public void Works_out_a_total_when_the_proc_sends_none()
    {
        var report = CapitalGainsService.Map([Sale("ASX:CBA", "15 Mar 2026", 1000, 1200), Sale("ASX:BHP", "02 Aug 2025", 500, 450)]);
        Assert.Equal(150m, report.Total.Gain);
        Assert.Equal(1650m, report.Total.TotalProceeds);
    }

    [Fact]
    public void An_unreadable_date_is_kept_as_text_and_sorted_last()
    {
        var report = CapitalGainsService.Map([Sale("ASX:ZZZ", "mars 2026", 1, 1), Sale("ASX:AAA", "01 Jul 2025", 1, 1)]);
        Assert.Equal(["AAA", "ZZZ"], report.Lines.Select(l => l.StockCode));
        Assert.Null(report.Lines[1].DateSold);
        Assert.Equal("mars 2026", report.Lines[1].DateSoldText);
    }

    [Theory]
    [InlineData("30 Jun 2026", 2026, 6, 30)]
    [InlineData("01 Jul 2025", 2025, 7, 1)]
    public void Reads_style_106_dates(string text, int y, int m, int d) =>
        Assert.Equal(new DateOnly(y, m, d), CapitalGainsService.ParseDateSold(text));
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
