using PortfolioManager.Core.Payouts;

namespace PortfolioManager.Tests;

public class PayoutTests
{
    private static Dictionary<string, object?> Row(int? id, string date, double total, double franked = 0, double rental = 0, double repairs = 0) => new()
    {
        ["Payout ID"] = id,
        ["Stock Exchange"] = id is null ? "Total:" : "ASX",
        ["Stock Code"] = id is null ? null : "BHP ",
        ["Payout Date"] = id is null ? null : date,
        ["Total Gross"] = total,
        ["Total Net"] = total,
        ["Franked Amount"] = franked,
        ["Unfranked Amount"] = 0d,
        ["Interest"] = 0d,
        ["Trust Income"] = DBNull.Value,
        ["Franking Credit"] = franked * 3 / 7,
        ["Tax Withheld"] = 0d,
        ["Foreign Tax Withheld"] = 0d,
        ["Other Amount"] = 0d,
        ["Comment"] = id is null ? "" : "Interim",
        ["Has Payout Been Confirmed?"] = id is null ? "" : "Yes",
        ["Rental Income"] = rental,
        ["Repairs & Maintenance"] = repairs,
        ["iOrder"] = id is null ? 1 : 0,
    };

    [Fact]
    public void Separates_the_total_row_and_lists_newest_first()
    {
        var summary = PayoutService.Map([Row(1, "15 Mar 2025", 70, 70), Row(2, "20 Sep 2025", 140, 140), Row(null, "", 210, 210)]);

        Assert.Equal([2, 1], summary.Payouts.Select(p => p.PayoutId));
        Assert.Equal(new DateTime(2025, 9, 20), summary.Payouts[0].Date);
        Assert.Equal("BHP", summary.Payouts[0].StockCode);
        Assert.Equal(60m, summary.Payouts[0].FrankingCredit);
        Assert.True(summary.Payouts[0].Confirmed);
        Assert.Equal(210m, summary.Total.Total);
        Assert.Equal(90m, summary.Total.FrankingCredit);
    }

    [Fact]
    public void Missing_values_are_zero_and_a_total_is_worked_out_when_absent()
    {
        var summary = PayoutService.Map([Row(1, "1 Jul 2025", 50, 50), Row(2, "2 Jul 2025", 25, 25)]);
        Assert.Equal(0m, summary.Payouts[0].TrustIncome);
        Assert.Equal(75m, summary.Total.Total);
    }

    [Fact]
    public void Rental_columns_are_summed_and_mark_the_payout_as_rental()
    {
        var p = PayoutService.Map([Row(1, "1 Jul 2025", 800, rental: 1000, repairs: 200)]).Payouts[0];
        Assert.True(p.IsRental);
        Assert.Equal(1000m, p.RentalIncome);
        Assert.Equal(200m, p.RentalExpenses);
        Assert.False(PayoutService.Map([Row(2, "1 Jul 2025", 70, 70)]).Payouts[0].IsRental);
    }

    [Theory]
    [InlineData("05 Feb 2024", 2024, 2, 5)]
    [InlineData("5 Feb 2024", 2024, 2, 5)]
    public void Reads_the_procs_date_text(string text, int y, int m, int d) =>
        Assert.Equal(new DateTime(y, m, d), PayoutService.ParseDate(text));

    [Fact]
    public void Unreadable_dates_are_null() => Assert.Null(PayoutService.ParseDate("not a date"));

    [Theory]
    [InlineData(2025, 10, 15, 2025)]
    [InlineData(2026, 6, 30, 2025)]
    [InlineData(2026, 7, 1, 2026)]
    public void Financial_year_runs_july_to_june(int y, int m, int d, int startYear)
    {
        var (start, end) = PayoutService.FinancialYear(new DateTime(y, m, d));
        Assert.Equal(new DateTime(startYear, 7, 1), start);
        Assert.Equal(new DateTime(startYear + 1, 6, 30), end);
    }

    private static PayoutInput Dividend() => new()
    {
        StockCode = "CBA",
        StockExchangeCode = "ASX",
        Date = new DateTime(2025, 9, 25),
        Franked = 225m,
        FrankingCredit = 96.43m,
        Total = 225m,
    };

    [Fact]
    public void A_complete_dividend_is_valid() => Assert.Empty(Dividend().Validate());

    [Fact]
    public void Cash_received_must_match_income_less_tax_withheld()
    {
        var p = Dividend();
        p.Unfranked = 25m;
        p.TaxWithheld = 10m;
        p.Total = 240m;
        Assert.Empty(p.Validate());

        p.Total = 250m;
        Assert.Single(p.ValidateAmounts());
    }

    [Fact]
    public void The_franking_credit_is_not_part_of_the_cash()
    {
        var p = Dividend();
        p.Total = 225m + 96.43m;
        Assert.NotEmpty(p.ValidateAmounts());
    }

    [Fact]
    public void A_payout_needs_a_code_a_positive_total_and_a_breakdown()
    {
        Assert.NotEmpty(new PayoutInput { StockCode = "", Total = 10, Interest = 10 }.ValidateCode());
        Assert.NotEmpty(new PayoutInput { StockCode = "X", Total = 0 }.ValidateAmounts());
        Assert.NotEmpty(new PayoutInput { StockCode = "X", Total = 10 }.ValidateAmounts());
        Assert.NotEmpty(new PayoutInput { StockCode = "X", Total = 10, Interest = 10, Other = -1 }.ValidateAmounts());
        Assert.Empty(new PayoutInput { StockCode = "Savings", StockExchangeCode = "CASH", Total = 12.5m, Interest = 12.5m }.Validate());
    }

    [Fact]
    public void Dates_outside_what_smalldatetime_holds_are_rejected() =>
        Assert.NotEmpty(new PayoutInput { Date = new DateTime(1850, 1, 1) }.ValidateDetails());
}
