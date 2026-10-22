using PortfolioManager.Core.Holdings;

namespace PortfolioManager.Tests;

public class HoldingsMapTests
{
    private static Dictionary<string, object?> Row(string? code, decimal value, decimal profit) => new()
    {
        ["strStockCode"] = code,
        ["strStockExchangeCode"] = code is null ? "TOTAL" : "ASX",
        ["Current Holding"] = code is null ? null : 100m,
        ["Market Price (Portfolio Currency)"] = code is null ? null : value / 100,
        ["Market Value (Portfolio Currency)"] = value,
        ["Total Capital Gain / Loss (Portfolio Currency)"] = profit,
        ["Total Payouts"] = 0m,
        ["Total Profit / Loss (Portfolio Currency)"] = profit,
        ["Trend Direction"] = code is null ? "" : "Bullish",
    };

    [Fact]
    public void Separates_the_total_row_wherever_it_appears_and_sorts_by_value()
    {
        var summary = HoldingsService.Map([Row("CBA", 500m, 50m), Row(null, 1700m, 30m), Row("BHP", 1200m, -20m)]);

        Assert.Equal(["BHP", "CBA"], summary.Holdings.Select(h => h.StockCode));
        Assert.Equal(1700m, summary.Total.MarketValue);
        Assert.Equal(30m, summary.Total.TotalProfit);
    }

    [Fact]
    public void Works_out_a_total_when_the_proc_sends_none()
    {
        var summary = HoldingsService.Map([Row("CBA", 500m, 50m), Row("BHP", 1200m, -20m)]);
        Assert.Equal(1700m, summary.Total.MarketValue);
        Assert.Equal(30m, summary.Total.TotalProfit);
    }

    [Fact]
    public void Missing_prices_show_as_zero()
    {
        var row = Row("XYZ", 0m, 0m);
        row["Market Price (Portfolio Currency)"] = DBNull.Value;
        Assert.Equal(0m, HoldingsService.Map([row]).Holdings[0].Price);
    }
}
