using System.Globalization;
using PortfolioManager.Core.Payouts;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Core.Regions;
using PortfolioManager.Core.Tax;

namespace PortfolioManager.Tests;

public class RegionSettingsTests
{
    private static readonly YearStart Uk = new(4, 6);

    [Fact]
    public void Blank_settings_are_Australian()
    {
        var r = RegionSettings.From(null, null, null, null, null);
        Assert.Equal(RegionSettings.Australia, r);
        Assert.True(r.IsAustralia);
    }

    [Fact]
    public void Blank_columns_take_the_countrys_usual_settings()
    {
        var r = RegionSettings.From("gb", null, null, " ", null);
        Assert.Equal(new RegionSettings("GB", Uk, "GBP", "en-GB"), r);
        Assert.False(r.IsAustralia);
    }

    [Fact]
    public void Saved_choices_win_over_the_country_defaults()
    {
        var r = RegionSettings.From("NZ", 7, 1, "aud", "en-AU");
        Assert.Equal(new RegionSettings("NZ", YearStart.July1, "AUD", "en-AU"), r);
    }

    [Fact]
    public void Unknown_or_invalid_values_fall_back()
    {
        var r = RegionSettings.From("ZZ", 13, 1, "AUD", "de-DE");
        Assert.Equal(RegionSettings.Australia, r);
        Assert.Equal(YearStart.July1, RegionSettings.From("AU", 2, 30, null, null).YearStart);
    }

    [Fact]
    public void Validate_lists_each_problem()
    {
        Assert.Empty(RegionSettings.Validate("US", 1, 1, "USD", "en-US"));
        Assert.Equal(4, RegionSettings.Validate("ZZ", 0, 1, "XYZ", "fr-FR").Count);
        Assert.Single(RegionSettings.Validate("AU", 7, 29, "AUD", "en-AU"));
    }

    [Fact]
    public void Every_country_default_is_a_valid_choice()
    {
        foreach (var c in RegionSettings.Countries)
            Assert.Empty(RegionSettings.Validate(c.Code, c.YearStart.Month, c.YearStart.Day, c.Currency, c.Format));
    }

    [Fact]
    public void Every_format_uses_a_full_stop_for_decimals()
    {
        // The number boxes always post 1234.56, which the site reads with the portfolio's format
        foreach (var f in RegionSettings.Formats)
        {
            var culture = new RegionSettings("XX", YearStart.January1, "USD", f.Culture).Culture();
            Assert.Equal(".", culture.NumberFormat.NumberDecimalSeparator);
            Assert.Equal(1234.56m, decimal.Parse("1234.56", culture));
        }
    }

    [Fact]
    public void Currency_symbol_follows_the_portfolio_currency()
    {
        Assert.Equal("$1,234.50", 1234.5m.ToString("C", RegionSettings.Australia.Culture()));
        Assert.Equal("£1,234.50", 1234.5m.ToString("C", new RegionSettings("GB", Uk, "GBP", "en-GB").Culture()));
        Assert.StartsWith("US$", 1234.5m.ToString("C", new RegionSettings("AU", YearStart.July1, "USD", "en-AU").Culture()));
        Assert.StartsWith("£", 1m.ToString("C", new RegionSettings("AU", YearStart.July1, "GBP", "en-AU").Culture()));
    }

    [Fact]
    public void Formats_dates_the_chosen_way()
    {
        var d = new DateTime(2026, 12, 31);
        Assert.Equal("31/12/2026", d.ToString("d", RegionSettings.Australia.Culture()));
        Assert.Equal("12/31/2026", d.ToString("d", new RegionSettings("US", YearStart.January1, "USD", "en-US").Culture()));
    }

    [Fact]
    public void Save_parameters()
    {
        var p = PortfolioService.RegionParameters(3, 9, new RegionSettings("GB", Uk, "GBP", "en-GB"));
        Assert.Equivalent(new
        {
            iUserId = 3, iPortfolioId = 9, strCountryCode = "GB", iYearStartMonth = 4, iYearStartDay = 6,
            strPortfolioCurrency = "GBP", strNumberFormat = "en-GB",
        }, p);
    }

    [Theory]
    [InlineData(2026, 4, 5, 2026)]
    [InlineData(2026, 4, 6, 2027)]
    public void Uk_year_turns_over_on_6_April(int y, int m, int d, int expected) =>
        Assert.Equal(expected, FinancialYear.EndYearFor(new DateOnly(y, m, d), Uk));

    [Fact]
    public void Uk_year_dates_and_label()
    {
        Assert.Equal(new DateOnly(2025, 4, 6), FinancialYear.StartDate(2026, Uk));
        Assert.Equal(new DateOnly(2026, 4, 5), FinancialYear.EndDate(2026, Uk));
        Assert.Equal("2025-26", FinancialYear.Label(2026, Uk));
    }

    [Fact]
    public void Calendar_year()
    {
        var jan = YearStart.January1;
        Assert.Equal(2026, FinancialYear.EndYearFor(new DateOnly(2026, 12, 31), jan));
        Assert.Equal(new DateOnly(2026, 1, 1), FinancialYear.StartDate(2026, jan));
        Assert.Equal(new DateOnly(2026, 12, 31), FinancialYear.EndDate(2026, jan));
        Assert.Equal("2026", FinancialYear.Label(2026, jan));
        Assert.Equal(2025, FinancialYear.LastCompleted(new DateOnly(2026, 10, 15), jan));
        var period = FinancialYear.Resolve(null, null, null, new DateOnly(2026, 10, 15), jan);
        Assert.Equal("Financial year 2025 (1 Jan 2025 to 31 Dec 2025)", period.Description);
    }

    [Fact]
    public void Financial_years_follow_the_current_portfolio()
    {
        try
        {
            RegionSettings.Current = new RegionSettings("GB", Uk, "GBP", "en-GB");
            Assert.Equal(new DateOnly(2026, 4, 6), FinancialYear.StartDate(2027));
            var (start, end) = PayoutService.FinancialYear(new DateTime(2026, 5, 1));
            Assert.Equal((new DateTime(2026, 4, 6), new DateTime(2027, 4, 5)), (start, end));
        }
        finally
        {
            RegionSettings.Current = RegionSettings.Australia;
        }
        Assert.Equal(new DateOnly(2026, 7, 1), FinancialYear.StartDate(2027));
    }

    [Fact]
    public void No_cgt_discount_outside_Australia()
    {
        var us = new RegionSettings("US", YearStart.January1, "USD", "en-US");
        Assert.Equal(CgtDiscountRate.None, CgtDiscount.RateFor("Individual", us));
        Assert.False(CgtDiscountRate.None.Applies);
        Assert.Equal(0.5m, CgtDiscount.RateFor("Individual", RegionSettings.Australia).Rate);

        var events = new[] { new CgtEvent("NYSE", "AAPL", new(2020, 1, 1), new(2026, 3, 1), 10, 1000, 3000) };
        var summary = CgtDiscount.Summarise(events, CgtDiscountRate.None);
        Assert.Equal(2000m, summary.DiscountGains);
        Assert.Equal(0m, summary.Discount);
        Assert.Equal(2000m, summary.NetCapitalGain);
    }
}
