using PortfolioManager.Core.Tax;

namespace PortfolioManager.Tests;

public class FinancialYearTests
{
    [Theory]
    [InlineData(2026, 6, 30, 2026)]
    [InlineData(2026, 7, 1, 2027)]
    [InlineData(2026, 1, 1, 2026)]
    [InlineData(2025, 12, 31, 2026)]
    public void End_year_turns_over_on_1_July(int y, int m, int d, int expected) =>
        Assert.Equal(expected, FinancialYear.EndYearFor(new DateOnly(y, m, d)));

    [Fact]
    public void Dates_run_1_July_to_30_June()
    {
        Assert.Equal(new DateOnly(2025, 7, 1), FinancialYear.StartDate(2026));
        Assert.Equal(new DateOnly(2026, 6, 30), FinancialYear.EndDate(2026));
    }

    [Theory]
    [InlineData(2026, "2025-26")]
    [InlineData(2000, "1999-00")]
    [InlineData(2010, "2009-10")]
    public void Labels(int endYear, string expected) => Assert.Equal(expected, FinancialYear.Label(endYear));

    [Theory]
    [InlineData(2026, 6, 30, 2025)]
    [InlineData(2026, 7, 1, 2026)]
    public void Last_completed_year_changes_on_1_July(int y, int m, int d, int expected) =>
        Assert.Equal(expected, FinancialYear.LastCompleted(new DateOnly(y, m, d)));

    [Fact]
    public void Recent_years_are_newest_first_starting_with_the_current_year()
    {
        var years = FinancialYear.Recent(new DateOnly(2026, 10, 15));
        Assert.Equal(11, years.Count);
        Assert.Equal(2027, years[0]);
        Assert.Equal(2017, years[^1]);
    }

    private static readonly DateOnly Today = new(2026, 10, 15);

    [Fact]
    public void Defaults_to_the_last_completed_year()
    {
        var p = FinancialYear.Resolve(null, null, null, Today);
        Assert.Equal(2026, p.FinancialYear);
        Assert.Equal(new DateOnly(2025, 7, 1), p.From);
        Assert.Equal(new DateOnly(2026, 6, 30), p.To);
        Assert.Null(p.Warning);
    }

    [Fact]
    public void A_financial_year_wins_over_stale_dates()
    {
        var p = FinancialYear.Resolve(2024, new DateOnly(2025, 7, 1), new DateOnly(2026, 6, 30), Today);
        Assert.Equal(2024, p.FinancialYear);
        Assert.Equal(new DateOnly(2023, 7, 1), p.From);
        Assert.Equal(new DateOnly(2024, 6, 30), p.To);
    }

    [Fact]
    public void Custom_dates_are_used_when_no_year_is_given()
    {
        var p = FinancialYear.Resolve(null, new DateOnly(2025, 1, 1), new DateOnly(2025, 3, 31), Today);
        Assert.Null(p.FinancialYear);
        Assert.Equal(new DateOnly(2025, 1, 1), p.From);
        Assert.Equal(new DateOnly(2025, 3, 31), p.To);
    }

    [Fact]
    public void Only_one_date_falls_back_to_the_default_year()
    {
        var p = FinancialYear.Resolve(null, new DateOnly(2025, 1, 1), null, Today);
        Assert.Equal(2026, p.FinancialYear);
    }

    [Fact]
    public void Back_to_front_dates_fall_back_with_a_warning()
    {
        var p = FinancialYear.Resolve(null, new DateOnly(2025, 3, 31), new DateOnly(2025, 1, 1), Today);
        Assert.Equal(2026, p.FinancialYear);
        Assert.NotNull(p.Warning);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(10000)]
    public void Nonsense_years_are_ignored(int fy) =>
        Assert.Equal(2026, FinancialYear.Resolve(fy, null, null, Today).FinancialYear);
}
