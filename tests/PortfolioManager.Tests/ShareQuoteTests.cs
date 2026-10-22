using PortfolioManager.Core.Prices;

namespace PortfolioManager.Tests;

public class ShareQuoteTests
{
    [Theory]
    [InlineData(12.0, 10.0, 1.0, ShareTrend.Bullish, 2.0)]
    [InlineData(8.5, 10.0, 1.0, ShareTrend.Bearish, -1.5)]
    [InlineData(10.5, 10.0, 1.0, ShareTrend.Neutral, 0.5)]
    [InlineData(11.0, 10.0, 1.0, ShareTrend.Neutral, 1.0)]
    public void Trend_compares_the_close_with_the_average_and_one_standard_deviation(
        double close, double sma, double sd, string status, double sds)
    {
        var t = ShareTrend.Read(close, sma, sd);
        Assert.Equal(status, t.Status);
        Assert.Equal(sds, t.StandardDeviations);
    }

    [Theory]
    [InlineData(null, 1.0)]
    [InlineData(10.0, null)]
    [InlineData(0.0, 1.0)]
    [InlineData(10.0, 0.0)]
    public void Trend_is_unknown_until_the_figures_exist(double? sma, double? sd)
    {
        var t = ShareTrend.Read(10, sma, sd);
        Assert.Equal(ShareTrend.Unknown, t.Status);
        Assert.Null(t.StandardDeviations);
    }

    [Theory]
    [InlineData("cba", "ASX", "CBA")]
    [InlineData(" NYSE : KO ", "NYSE", "KO")]
    [InlineData("ASX:BRK.B", "ASX", "BRK.B")]
    [InlineData("FOREX:USD/AUD", "FOREX", "USD/AUD")]
    public void Symbols_are_read(string text, string exchange, string code) =>
        Assert.Equal((exchange, code), QuoteService.ParseSymbol(text));

    [Theory]
    [InlineData("")]
    [InlineData("ASX:")]
    [InlineData(":CBA")]
    [InlineData("CBA'; DROP")]
    [InlineData("AS X:CBA")]
    public void Odd_symbols_are_refused(string text) => Assert.Null(QuoteService.ParseSymbol(text));

    private static PriceBar Bar(int day, double close) => new(new DateOnly(2026, 10, day), null, null, null, close, null, null, null);

    [Fact]
    public void Quote_works_out_the_change_and_year_range()
    {
        var q = new Quote("ASX", "CBA", "Commonwealth Bank", null, [Bar(1, 100), Bar(2, 110), Bar(3, 99)], 0);
        Assert.Equal(-11, q.Change!.Value, 9);
        Assert.Equal(-0.1, q.ChangePct!.Value, 9);
        Assert.Equal((99, 110), q.YearRange);
    }

    [Fact]
    public void Trend_row_week_change_needs_an_earlier_price()
    {
        Assert.Null(new TrendRow("ASX", "CBA", null, new DateOnly(2026, 10, 1), 10, null, null, null).WeekChange);
        Assert.Equal(0.25, new TrendRow("ASX", "CBA", null, new DateOnly(2026, 10, 1), 10, 8, null, null).WeekChange!.Value, 9);
    }
}
