using PortfolioManager.Core.Performance;

namespace PortfolioManager.Tests;

public class PerformanceTests
{
    private static DateOnly D(int month, int day) => new(2026, month, day);

    private static PerformanceInput Input(
        PerfTrade[]? trades = null, PerfCash[]? cash = null, PerfPrice[]? prices = null,
        PerfSplit[]? splits = null, PerfIncome[]? income = null, string currency = "AUD") =>
        new(currency, trades ?? [], cash ?? [], prices ?? [], splits ?? [], income ?? []);

    private static PerfTrade Buy(DateOnly date, string code, double qty, double price, bool hasCash = true, bool drp = false) =>
        new(date, "ASX", code, "Buy", qty, (decimal)(qty * price), price, "AUD", hasCash, drp);

    private static PerfPrice Price(string code, DateOnly date, double close) => new("ASX", code, date, close);

    [Fact]
    public void Buying_shares_with_cash_is_not_a_gain()
    {
        // $10,000 deposited, then spent on 1,000 CBA at $10 the next day; the price doesn't move
        var input = Input(
            trades: [Buy(D(7, 2), "CBA", 1000, 10)],
            cash: [new(D(7, 1), 10000, 0, "AUD", false), new(D(7, 2), 0, 10000, "AUD", true)],
            prices: [Price("CBA", D(6, 30), 10)]);

        var r = PerformanceCalculator.Calculate(input, D(7, 1), D(7, 5));

        Assert.Equal(0, r.Summary.TimeWeightedReturn, 9);
        Assert.Equal(10000m, r.Summary.MoneyIn);
        Assert.Equal(0m, r.Summary.MoneyOut);
        Assert.Equal(10000m, r.Summary.EndValue);
        Assert.Equal(0m, r.Summary.Growth);
    }

    [Fact]
    public void Price_rise_is_the_return_whatever_money_is_added_later()
    {
        // 100 units at $10 rise to $11 (+10%), then $1,000 more is added and the price stays put
        var input = Input(
            trades: [Buy(D(6, 1), "BHP", 100, 10, hasCash: false)],
            cash: [new(D(7, 3), 1000, 0, "AUD", false)],
            prices: [Price("BHP", D(6, 30), 10), Price("BHP", D(7, 2), 11)]);

        var r = PerformanceCalculator.Calculate(input, D(7, 1), D(7, 4));

        Assert.Equal(0.10, r.Summary.TimeWeightedReturn, 9);
        Assert.Equal(1000m, r.Summary.StartValue);
        Assert.Equal(2100m, r.Summary.EndValue);
        Assert.Equal(100m, r.Summary.Growth);
    }

    [Fact]
    public void Shares_transferred_in_without_cash_count_as_money_added()
    {
        var input = Input(
            trades: [Buy(D(7, 2), "WES", 10, 50, hasCash: false)],
            prices: [Price("WES", D(7, 1), 50)]);

        var r = PerformanceCalculator.Calculate(input, D(7, 1), D(7, 3));

        Assert.Equal(500m, r.Summary.MoneyIn);
        Assert.Equal(0, r.Summary.TimeWeightedReturn, 9);
    }

    [Fact]
    public void Reinvested_dividend_units_are_not_money_added()
    {
        // A DRP buy has no cash and is paid for by the dividend, so it adds to the return
        var input = Input(
            trades: [Buy(D(6, 1), "CBA", 100, 10, hasCash: false), Buy(D(7, 2), "CBA", 10, 10, hasCash: false, drp: true)],
            prices: [Price("CBA", D(6, 1), 10)]);

        var r = PerformanceCalculator.Calculate(input, D(7, 1), D(7, 3));

        Assert.Equal(0m, r.Summary.MoneyIn);
        Assert.Equal(0.10, r.Summary.TimeWeightedReturn, 9);
    }

    [Fact]
    public void Cash_dividend_counts_as_return_even_when_not_banked()
    {
        var input = Input(
            trades: [Buy(D(6, 1), "CBA", 100, 10, hasCash: false)],
            prices: [Price("CBA", D(6, 1), 10)],
            income: [new(D(7, 2), 50)]);

        var r = PerformanceCalculator.Calculate(input, D(7, 1), D(7, 3));

        Assert.Equal(0.05, r.Summary.TimeWeightedReturn, 9);
        Assert.Equal(50m, r.Summary.TotalGain);
    }

    [Fact]
    public void Cash_dividend_also_entered_as_a_deposit_is_not_counted_twice()
    {
        var input = Input(
            trades: [Buy(D(6, 1), "CBA", 100, 10, hasCash: false)],
            cash: [new(D(7, 2), 50, 0, "AUD", false)],
            prices: [Price("CBA", D(6, 1), 10)],
            income: [new(D(7, 2), 50)]);

        var r = PerformanceCalculator.Calculate(input, D(7, 1), D(7, 3));

        Assert.Equal(0.05, r.Summary.TimeWeightedReturn, 9);
        Assert.Equal(50m, r.Summary.TotalGain);
    }

    [Fact]
    public void Prices_before_a_split_are_restated_in_the_new_units()
    {
        // 400 units after a 4:1 split on 1 Jul (the trade was restated from 100); old price $40 is $10 now
        var input = Input(
            trades: [Buy(D(6, 1), "XYZ", 400, 10, hasCash: false)],
            prices: [Price("XYZ", D(6, 30), 40), Price("XYZ", D(7, 2), 10)],
            splits: [new("ASX", "XYZ", D(7, 1), 4)]);

        var r = PerformanceCalculator.Calculate(input, D(7, 1), D(7, 3));

        Assert.Equal(4000m, r.Summary.StartValue);
        Assert.Equal(0, r.Summary.TimeWeightedReturn, 9);
    }

    [Fact]
    public void Foreign_holdings_are_converted_with_the_rate_on_file()
    {
        var trade = new PerfTrade(D(6, 1), "NYSE", "KO", "Buy", 10, 900, 60, "USD", false, false);
        var input = Input(
            trades: [trade],
            prices: [new("NYSE", "KO", D(6, 1), 60), new("FOREX", "USD/AUD", D(6, 1), 1.5), new("FOREX", "USD/AUD", D(7, 2), 1.6)]);

        var r = PerformanceCalculator.Calculate(input, D(7, 1), D(7, 2));

        Assert.Equal(900m, r.Summary.StartValue);
        Assert.Equal(960m, r.Summary.EndValue);
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void Missing_price_falls_back_to_the_last_trade_price_with_a_warning()
    {
        var input = Input(trades: [Buy(D(6, 1), "PRIV", 100, 2, hasCash: false)]);

        var r = PerformanceCalculator.Calculate(input, D(7, 1), D(7, 2));

        Assert.Equal(200m, r.Summary.EndValue);
        Assert.Contains(r.Warnings, w => w.Contains("ASX:PRIV"));
    }

    [Fact]
    public void Empty_portfolio_has_no_return()
    {
        var r = PerformanceCalculator.Calculate(Input(), D(7, 1), D(7, 31));
        Assert.Equal(31, r.Days.Count);
        Assert.Equal(0, r.Summary.TimeWeightedReturn);
        Assert.Single(r.Months);
    }

    [Fact]
    public void Daily_return_leaves_out_money_moved_that_day()
    {
        Assert.Equal(0.1, PerformanceCalculator.DailyReturn(1000, 2100, 1000, 0, 0), 9);
        Assert.Equal(0.1, PerformanceCalculator.DailyReturn(1000, 600, 0, 500, 0), 9);
        Assert.Equal(0.1, PerformanceCalculator.DailyReturn(0, 1100, 1000, 0, 0), 9);
        Assert.Equal(0, PerformanceCalculator.DailyReturn(0, 0, 0, 0, 0));
    }

    [Fact]
    public void Months_chain_their_daily_returns()
    {
        var input = Input(
            trades: [Buy(D(6, 1), "CBA", 100, 10, hasCash: false)],
            prices: [Price("CBA", D(6, 1), 10), Price("CBA", D(7, 15), 11), Price("CBA", D(8, 15), 12.1)]);

        var r = PerformanceCalculator.Calculate(input, D(7, 1), D(8, 31));

        Assert.Equal(2, r.Months.Count);
        Assert.Equal(0.10, r.Months[0].Return, 9);
        Assert.Equal(0.10, r.Months[1].Return, 9);
        Assert.Equal(0.21, r.Summary.TimeWeightedReturn, 9);
    }

    [Theory]
    [InlineData("asx:stw", "ASX", "STW")]
    [InlineData("STW", "ASX", "STW")]
    [InlineData("INDEX:XJO", "INDEX", "XJO")]
    public void Benchmark_keys_are_read(string key, string exchange, string code) =>
        Assert.Equal((exchange, code), Benchmark.Parse(key));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ASX:")]
    [InlineData("ASX:ST W;--")]
    public void Odd_benchmark_keys_are_refused(string? key) => Assert.Null(Benchmark.Parse(key));

    [Fact]
    public void Benchmark_is_rebased_on_the_day_before_the_period()
    {
        var points = new BenchmarkPoint[] { new(D(6, 30), 100), new(D(7, 2), 105), new(D(8, 1), 110.25) };
        var b = Benchmark.Compare(new("ASX:STW", "STW"), points, D(7, 1), D(8, 1));

        Assert.Equal(0.1025, b.Return!.Value, 9);
        Assert.Equal(0, b.Days[0].Cumulative, 9);
        Assert.Equal(0.05, b.Days[1].Cumulative, 9);
        Assert.Equal(0.05, b.Months[0].Return, 9);
        Assert.Equal(0.05, b.Months[1].Return, 9);
    }

    [Fact]
    public void Benchmark_with_no_prices_has_no_return() =>
        Assert.Null(Benchmark.Compare(new("ASX:STW", "STW"), [], D(7, 1), D(7, 31)).Return);
}
