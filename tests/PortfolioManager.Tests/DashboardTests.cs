using PortfolioManager.Core.Dashboard;
using PortfolioManager.Core.Holdings;
using PortfolioManager.Core.Payouts;
using PortfolioManager.Core.Performance;
using PortfolioManager.Core.Regions;

namespace PortfolioManager.Tests;

public class DashboardTests
{
    private static readonly DateOnly Today = new(2026, 10, 27);

    private static Holding H(string code, decimal units, decimal value, decimal cost, string exchange = "ASX") =>
        new(code, exchange, units, units == 0 ? 0 : value / units, value, value - cost, 0, value - cost, "", cost);

    private static PerformanceDay Day(int day, decimal marketValue, decimal cash = 0, decimal moneyIn = 0, decimal moneyOut = 0, decimal income = 0, double ret = 0) =>
        new(new DateOnly(2026, 10, day), cash, marketValue, cash + marketValue, moneyIn, moneyOut, income, ret, 0);

    private static Payout P(DateTime date, decimal total, decimal franked = 0, decimal unfranked = 0, decimal rent = 0) =>
        new(1, "ASX", "CBA", date, total, franked, unfranked, 0, 0, 0, 0, 0, 0, rent, 0, "", true);

    [Theory]
    [InlineData("1M", 2026, 9, 27)]
    [InlineData("3m", 2026, 7, 27)]
    [InlineData("FYTD", 2026, 7, 1)]
    [InlineData("1Y", 2025, 10, 27)]
    [InlineData("rubbish", 2026, 7, 1)]
    public void Chart_periods_start_on_the_right_day(string key, int y, int m, int d) =>
        Assert.Equal(new DateOnly(y, m, d), DashboardFigures.PeriodStart(key, Today, YearStart.July1));

    [Fact]
    public void This_financial_year_follows_the_portfolios_year_start()
    {
        Assert.Equal(new DateOnly(2026, 1, 1), DashboardFigures.PeriodStart("FYTD", Today, YearStart.January1));
        Assert.Equal("FYTD", DashboardFigures.NormalPeriod(null));
        Assert.Equal("1Y", DashboardFigures.NormalPeriod("1y"));
    }

    [Fact]
    public void Leading_days_with_nothing_held_are_dropped()
    {
        var days = new[] { Day(1, 0), Day(2, 0), Day(3, 100, moneyIn: 100), Day(4, 110) };
        var trimmed = DashboardFigures.SinceFirstHolding(days);
        Assert.Equal(2, trimmed.Count);
        Assert.Equal(3, trimmed[0].Date.Day);
    }

    [Fact]
    public void Long_charts_are_thinned_to_weekly_but_keep_the_last_day()
    {
        Assert.Equal(10, DashboardFigures.ChartIndexes(10).Count);
        var idx = DashboardFigures.ChartIndexes(1000);
        Assert.Equal(0, idx[0]);
        Assert.Equal(7, idx[1]);
        Assert.Equal(999, idx[^1]);
    }

    [Fact]
    public void The_latest_move_skips_the_weekend_and_ignores_money_added()
    {
        // Friday the 23rd moved; Saturday and Sunday didn't. Money added on Friday isn't a gain.
        var days = new[]
        {
            Day(22, 1000, cash: 500),
            Day(23, 1050, cash: 700, moneyIn: 200, ret: 0.0333),
            Day(24, 1050, cash: 700),
            Day(25, 1050, cash: 700),
        };
        var move = DashboardFigures.LatestMove(days);
        Assert.NotNull(move);
        Assert.Equal(23, move.Date.Day);
        Assert.Equal(50, move.Change);
        Assert.Equal(0.0333, move.Return);
    }

    [Fact]
    public void No_move_when_prices_havent_changed()
    {
        Assert.Null(DashboardFigures.LatestMove([Day(1, 100), Day(2, 100)]));
        Assert.Null(DashboardFigures.LatestMove([]));
    }

    [Fact]
    public void Top_holdings_are_the_biggest_with_their_weight()
    {
        var top = DashboardFigures.Top([H("CBA", 10, 300, 200), H("BHP", 10, 100, 0), H("SOLD", 0, 0, 0), H("VAS", 5, 600, 650)], 2);
        Assert.Equal(["VAS", "CBA"], top.Select(t => t.Holding.StockCode));
        Assert.Equal(0.6m, top[0].Weight);
        Assert.Equal(-50m / 650m, top[0].GainPercent);
        Assert.Null(DashboardFigures.Top([H("BHP", 10, 100, 0)])[0].GainPercent);
    }

    [Fact]
    public void Allocation_by_type_splits_australian_overseas_crypto_property_and_cash()
    {
        var slices = DashboardFigures.ByType(
            [H("CBA", 10, 400, 300), H("AAPL", 2, 300, 200, "NASDAQ"), H("BTC-AUD", 0.1m, 200, 100, "CRYPTO"), H("SOLD", 0, 0, 0)],
            cash: 100, propertyEquity: -50, australia: true);
        Assert.Equal(["Australian shares and funds", "Overseas shares and funds", "Crypto", "Cash"], slices.Select(s => s.Label));
        Assert.Equal(0.4m, slices[0].Share);
        Assert.Equal(1m, slices.Sum(s => s.Share));

        var elsewhere = DashboardFigures.ByType([H("CBA", 10, 400, 300), H("AAPL", 2, 300, 200, "NASDAQ")], 0, 0, australia: false);
        Assert.Equal("Shares and funds", Assert.Single(elsewhere).Label);
    }

    [Fact]
    public void Income_months_split_received_and_start_projections_this_month()
    {
        var months = DashboardFigures.IncomeMonths(
            [
                P(new DateTime(2026, 9, 20), 100, franked: 70, unfranked: 20),
                P(new DateTime(2026, 9, 25), 500, rent: 500),
                P(new DateTime(2025, 10, 2), 40, franked: 40),
            ],
            [
                new ProjectedPayout("ASX", "CBA", new DateTime(2026, 9, 20), 99),
                new ProjectedPayout("ASX", "CBA", new DateTime(2026, 10, 30), 80),
                new ProjectedPayout("ASX", "CBA", new DateTime(2027, 3, 20), 90),
            ],
            Today);
        Assert.Equal(24, months.Count);
        Assert.Equal(new DateTime(2025, 11, 1), months[0].Month);
        Assert.Equal(new DateTime(2027, 10, 1), months[^1].Month);
        var sep = months.Single(m => m.Month == new DateTime(2026, 9, 1));
        Assert.Equal((70m, 20m, 10m, 0m), (sep.Franked, sep.Unfranked, sep.Other, sep.Projected));
        Assert.Equal(80, months.Single(m => m.Month == new DateTime(2026, 10, 1)).Projected);
        Assert.Equal(90, months.Single(m => m.Month == new DateTime(2027, 3, 1)).Projected);
        Assert.DoesNotContain(months, m => m.Month == new DateTime(2025, 10, 1));
    }

    [Theory]
    [InlineData(3, "2026-10-27", "2026-07-01", "2026-09-30", "2026-10-28")]
    [InlineData(3, "2026-10-29", "2026-10-01", "2026-12-31", "2027-02-28")]
    [InlineData(3, "2027-01-10", "2026-10-01", "2026-12-31", "2027-02-28")]
    [InlineData(1, "2026-10-27", "2026-10-01", "2026-10-31", "2026-11-21")]
    [InlineData(1, "2026-10-15", "2026-09-01", "2026-09-30", "2026-10-21")]
    [InlineData(12, "2026-08-01", "2025-07-01", "2026-06-30", "2026-10-31")]
    [InlineData(2, "2026-09-10", "2026-07-01", "2026-08-31", "2026-09-28")]
    public void Next_bas_due_date(int months, string today, string from, string to, string due)
    {
        var bas = DashboardFigures.NextBas(DateOnly.Parse(today), months);
        Assert.NotNull(bas);
        Assert.Equal((DateOnly.Parse(from), DateOnly.Parse(to), DateOnly.Parse(due)), (bas.From, bas.To, bas.Due));
    }

    [Fact]
    public void Odd_bas_periods_have_no_due_date() => Assert.Null(DashboardFigures.NextBas(Today, 4));

    [Fact]
    public void Stale_prices_and_missing_costs_are_flagged()
    {
        var holdings = new[] { H("CBA", 10, 400, 300), H("OLD", 10, 50, 40), H("NONE", 5, 10, 0), H("SOLD", 0, 0, 0), H("USD", 100, 150, 150, "FOREX") };
        var dates = new Dictionary<string, DateTime>
        {
            [DashboardFigures.PriceKey("asx", "cba ")] = new(2026, 10, 24),
            [DashboardFigures.PriceKey("ASX", "OLD")] = new(2026, 10, 1),
        };
        Assert.Equal(["OLD", "NONE"], DashboardFigures.StalePrices(holdings, dates, Today).Select(h => h.StockCode));
        Assert.Equal(["NONE"], DashboardFigures.MissingCost(holdings).Select(h => h.StockCode));
    }

    [Fact]
    public void Loss_hint_only_near_the_year_end()
    {
        Holding[] holdings = [H("CBA", 10, 400, 300), H("DOWN", 10, 50, 80), H("DOWN2", 10, 10, 30)];
        Assert.Null(DashboardFigures.HarvestHint(holdings, Today, YearStart.July1));
        var hint = DashboardFigures.HarvestHint(holdings, new DateOnly(2027, 5, 15), YearStart.July1);
        Assert.NotNull(hint);
        Assert.Equal((50m, 2, new DateOnly(2027, 6, 30)), hint.Value);
        Assert.Null(DashboardFigures.HarvestHint([H("CBA", 10, 400, 300)], new DateOnly(2027, 5, 15), YearStart.July1));
    }
}
