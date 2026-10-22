using PortfolioManager.Core.Reports;

namespace PortfolioManager.Tests;

public class IncomeCalendarTests
{
    [Fact]
    public void A_financial_year_has_twelve_months_from_july()
    {
        var months = IncomeByMonth.MonthsOf(new DateOnly(2026, 7, 1), new DateOnly(2027, 6, 30));
        Assert.Equal(12, months.Count);
        Assert.Equal((2026, 7), months[0]);
        Assert.Equal((2027, 6), months[^1]);
    }

    [Fact]
    public void Totals_by_holding_and_month()
    {
        var m = new IncomeByMonth(IncomeByMonth.MonthsOf(new DateOnly(2026, 7, 1), new DateOnly(2027, 6, 30)),
        [
            new("ASX", "CBA", 2026, 9, 420, 180),
            new("ASX", "CBA", 2027, 3, 400, 171.43m),
            new("ASX", "VAS", 2026, 10, 150, 20),
        ]);

        Assert.Equal(["CBA", "VAS"], m.Holdings.Select(h => h.StockCode));
        Assert.Equal(820m, m.HoldingTotal("ASX", "CBA"));
        Assert.Equal(420m, m.MonthTotal(2026, 9));
        Assert.Equal(0m, m.Cash("ASX", "VAS", 2026, 9));
        Assert.Equal(970m, m.Total);
        Assert.Equal(371.43m, m.FrankingCredits);
    }

    [Fact]
    public void Upcoming_estimate_is_units_times_amount()
    {
        Assert.Equal(1234.57m, new UpcomingIncome("ASX", "CBA", new DateOnly(2027, 2, 1), null, 1.234567m, "AUD", 1000, false).Estimate);
    }
}
