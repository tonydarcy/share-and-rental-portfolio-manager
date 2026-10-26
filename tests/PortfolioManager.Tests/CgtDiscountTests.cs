using PortfolioManager.Core.Tax;

namespace PortfolioManager.Tests;

public class CgtDiscountTests
{
    private static readonly CgtDiscountRate Individual = CgtDiscount.RateFor("Individual");
    private static readonly CgtDiscountRate Super = CgtDiscount.RateFor("Superfund");
    private static readonly CgtDiscountRate Company = CgtDiscount.RateFor("Company");

    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    private static CgtEvent Gain(decimal gain, bool discount, DateOnly? sold = null)
    {
        var disposed = sold ?? D(2026, 3, 1);
        var acquired = discount ? disposed.AddYears(-2) : disposed.AddMonths(-3);
        return new CgtEvent("ASX", "TST", acquired, disposed, 1, gain < 0 ? -gain : 0, gain < 0 ? 0 : gain);
    }

    // The 12-month rule

    [Theory]
    [InlineData("2024-07-01", "2025-07-01", false)] // the anniversary itself isn't enough
    [InlineData("2024-07-01", "2025-07-02", true)]
    [InlineData("2024-07-01", "2024-12-31", false)]
    [InlineData("2024-07-01", "2024-07-01", false)]
    [InlineData("2024-02-29", "2025-02-28", false)] // bought on a leap day: anniversary is 28 Feb
    [InlineData("2024-02-29", "2025-03-01", true)]
    [InlineData("2023-02-28", "2024-02-28", false)]
    [InlineData("2023-02-28", "2024-02-29", true)]  // the day after the anniversary is a leap day
    [InlineData("2023-03-01", "2024-03-01", false)] // 366 days later but still only the anniversary
    [InlineData("2023-03-01", "2024-03-02", true)]
    [InlineData("2024-12-31", "2026-01-01", true)]
    public void Qualifies_only_after_the_anniversary(string bought, string sold, bool expected) =>
        Assert.Equal(expected, CgtDiscount.Qualifies(DateOnly.Parse(bought), DateOnly.Parse(sold)));

    [Theory]
    [InlineData("2024-07-01", "2025-07-02")]
    [InlineData("2024-02-29", "2025-03-01")]
    [InlineData("2023-02-28", "2024-02-29")]
    [InlineData("2025-06-30", "2026-07-01")]
    public void Eligible_from_is_the_day_after_the_anniversary(string bought, string expected) =>
        Assert.Equal(DateOnly.Parse(expected), CgtDiscount.EligibleFrom(DateOnly.Parse(bought)));

    // Entity types

    [Theory]
    [InlineData("Individual", CgtEntityKind.Individual, 0.5, false)]
    [InlineData(" individual ", CgtEntityKind.Individual, 0.5, false)]
    [InlineData("Family Trust", CgtEntityKind.Trust, 0.5, false)]
    [InlineData("Company", CgtEntityKind.Company, 0, false)]
    [InlineData("Smith Investments Pty Ltd", CgtEntityKind.Company, 0, false)]
    [InlineData("Superfund", CgtEntityKind.SuperFund, 0.3333, false)]
    [InlineData("Super Fund", CgtEntityKind.SuperFund, 0.3333, false)]
    [InlineData("SMSF", CgtEntityKind.SuperFund, 0.3333, false)]
    [InlineData("Other", CgtEntityKind.Unknown, 0.5, true)]
    [InlineData("", CgtEntityKind.Unknown, 0.5, true)]
    [InlineData(null, CgtEntityKind.Unknown, 0.5, true)]
    public void Rate_by_entity_type(string? type, CgtEntityKind kind, double rate, bool assumed)
    {
        var r = CgtDiscount.RateFor(type);
        Assert.Equal(kind, r.Kind);
        Assert.Equal((decimal)rate, Math.Round(r.Rate, 4));
        Assert.Equal(assumed, r.Assumed);
    }

    [Fact]
    public void Rate_text()
    {
        Assert.Equal("50%", Individual.RateText);
        Assert.Equal("33⅓%", Super.RateText);
        Assert.Equal("0%", Company.RateText);
    }

    [Fact]
    public void Individual_gets_half_off_discount_gains()
    {
        var s = CgtDiscount.Summarise([Gain(1000, true)], Individual);
        Assert.Equal(500m, s.Discount);
        Assert.Equal(500m, s.NetCapitalGain);
    }

    [Fact]
    public void Trust_gets_half_off_discount_gains() =>
        Assert.Equal(500m, CgtDiscount.Summarise([Gain(1000, true)], CgtDiscount.RateFor("Trust")).NetCapitalGain);

    [Fact]
    public void Super_fund_gets_a_third_off_discount_gains()
    {
        var s = CgtDiscount.Summarise([Gain(300, true)], Super);
        Assert.Equal(100m, s.Discount);
        Assert.Equal(200m, s.NetCapitalGain);
    }

    [Fact]
    public void Super_fund_discount_is_rounded_to_cents()
    {
        var s = CgtDiscount.Summarise([Gain(100.01m, true)], Super);
        Assert.Equal(33.34m, s.Discount);
        Assert.Equal(66.67m, s.NetCapitalGain);
    }

    [Fact]
    public void Company_gets_no_discount()
    {
        var s = CgtDiscount.Summarise([Gain(1000, true), Gain(200, false)], Company);
        Assert.Equal(0m, s.Discount);
        Assert.Equal(1200m, s.NetCapitalGain);
    }

    [Fact]
    public void Unknown_entity_type_uses_half() =>
        Assert.Equal(500m, CgtDiscount.Summarise([Gain(1000, true)], CgtDiscount.RateFor("Other")).NetCapitalGain);

    [Fact]
    public void Gains_are_not_discounted_when_held_12_months_or_less()
    {
        var s = CgtDiscount.Summarise([Gain(1000, false)], Individual);
        Assert.Equal(0m, s.DiscountGains);
        Assert.Equal(1000m, s.OtherGains);
        Assert.Equal(1000m, s.NetCapitalGain);
    }

    // Loss ordering

    [Fact]
    public void Losses_go_against_other_gains_before_discount_gains()
    {
        var s = CgtDiscount.Summarise([Gain(1000, true), Gain(400, false), Gain(-600, false)], Individual);
        Assert.Equal(400m, s.LossesAppliedToOtherGains);
        Assert.Equal(200m, s.LossesAppliedToDiscountGains);
        Assert.Equal(400m, s.Discount); // half of the 800 discount gains left
        Assert.Equal(400m, s.NetCapitalGain);
        Assert.Equal(0m, s.LossCarriedForward);
    }

    [Fact]
    public void Small_loss_only_reduces_other_gains()
    {
        var s = CgtDiscount.Summarise([Gain(1000, true), Gain(400, false), Gain(-100, true)], Individual);
        Assert.Equal(100m, s.LossesAppliedToOtherGains);
        Assert.Equal(0m, s.LossesAppliedToDiscountGains);
        Assert.Equal(500m, s.Discount);
        Assert.Equal(800m, s.NetCapitalGain);
    }

    [Fact]
    public void Discount_applies_after_losses_not_before()
    {
        // 1000 discount gain less 400 loss = 600, halved = 300 (not 500 - 400 = 100)
        var s = CgtDiscount.Summarise([Gain(1000, true), Gain(-400, true)], Individual);
        Assert.Equal(300m, s.Discount);
        Assert.Equal(300m, s.NetCapitalGain);
    }

    [Fact]
    public void Losses_on_long_held_parcels_are_still_losses()
    {
        var s = CgtDiscount.Summarise([Gain(-250, true), Gain(-50, false)], Individual);
        Assert.Equal(300m, s.CurrentYearLosses);
    }

    [Fact]
    public void All_loss_year_carries_the_loss_forward()
    {
        var s = CgtDiscount.Summarise([Gain(200, true), Gain(100, false), Gain(-700, false)], Individual);
        Assert.Equal(300m, s.GrossGains);
        Assert.Equal(300m, s.LossesApplied);
        Assert.Equal(0m, s.Discount);
        Assert.Equal(0m, s.NetCapitalGain);
        Assert.Equal(400m, s.LossCarriedForward);
    }

    [Fact]
    public void Only_losses()
    {
        var s = CgtDiscount.Summarise([Gain(-500, false)], Individual);
        Assert.Equal(0m, s.GrossGains);
        Assert.Equal(0m, s.LossesApplied);
        Assert.Equal(0m, s.NetCapitalGain);
        Assert.Equal(500m, s.LossCarriedForward);
    }

    [Fact]
    public void No_sales_is_all_zero()
    {
        var s = CgtDiscount.Summarise([], Individual);
        Assert.Equal(0m, s.GrossGains);
        Assert.Equal(0m, s.NetCapitalGain);
        Assert.Equal(0m, s.LossCarriedForward);
    }

    [Fact]
    public void Prior_year_losses_follow_the_same_order()
    {
        var s = CgtDiscount.Summarise([Gain(1000, true), Gain(100, false)], Individual, priorYearLosses: 300);
        Assert.Equal(300m, s.PriorYearLosses);
        Assert.Equal(100m, s.LossesAppliedToOtherGains);
        Assert.Equal(200m, s.LossesAppliedToDiscountGains);
        Assert.Equal(400m, s.NetCapitalGain);
    }

    [Fact]
    public void Losses_brought_forward_run_year_by_year()
    {
        var events = new[]
        {
            Gain(-1000, false, D(2024, 3, 1)), // FY2024: 1000 loss
            Gain(400, false, D(2024, 9, 1)),   // FY2025: uses 400
            Gain(1000, true, D(2026, 3, 1)),   // FY2026
        };
        Assert.Equal(0m, CgtDiscount.LossesBroughtForward(events, 2024, Individual));
        Assert.Equal(1000m, CgtDiscount.LossesBroughtForward(events, 2025, Individual));
        Assert.Equal(600m, CgtDiscount.LossesBroughtForward(events, 2026, Individual));
        Assert.Equal(0m, CgtDiscount.LossesBroughtForward(events, 2027, Individual));
    }

    [Fact]
    public void Mixed_parcels_worked_example()
    {
        // FY2025-26 for an individual: a discount gain, a short-term gain and a loss
        var trades = new[]
        {
            new TradeLot(1, "ASX", "BHP", "BUY", D(2024, 7, 1), 100, 40m),
            new TradeLot(2, "ASX", "CBA", "BUY", D(2025, 1, 1), 10, 150m),
            new TradeLot(3, "ASX", "BHP", "SELL", D(2025, 7, 2), 100, 50m),
            new TradeLot(4, "ASX", "XYZ", "BUY", D(2025, 8, 1), 200, 5m),
            new TradeLot(5, "ASX", "CBA", "SELL", D(2025, 10, 1), 10, 190m),
            new TradeLot(6, "ASX", "XYZ", "SELL", D(2026, 3, 1), 200, 3.5m),
        };
        var r = CgtDiscountService.Build(CgtParcels.Match(trades), Individual, FinancialYear.Resolve(2026, null, null, D(2026, 10, 15)));
        var s = r.Summary;

        Assert.Equal(1000m, s.DiscountGains);
        Assert.Equal(400m, s.OtherGains);
        Assert.Equal(1400m, s.GrossGains);
        Assert.Equal(300m, s.CurrentYearLosses);
        Assert.Equal(0m, s.PriorYearLosses);
        Assert.Equal(300m, s.LossesApplied);
        Assert.Equal(300m, s.LossesAppliedToOtherGains);
        Assert.Equal(500m, s.Discount);
        Assert.Equal(600m, s.NetCapitalGain);
        Assert.Equal(0m, s.LossCarriedForward);
        Assert.True(r.PriorYearLossesIncluded);
        Assert.Equal(1100m, r.TotalGain);
    }

    [Fact]
    public void Build_keeps_only_the_period_and_brings_losses_forward_for_a_financial_year()
    {
        var trades = new[]
        {
            new TradeLot(1, "ASX", "AAA", "BUY", D(2024, 1, 10), 100, 10m),
            new TradeLot(2, "ASX", "AAA", "SELL", D(2024, 5, 10), 100, 7m),  // FY2024: 300 loss
            new TradeLot(3, "ASX", "BBB", "BUY", D(2024, 1, 10), 100, 10m),
            new TradeLot(4, "ASX", "BBB", "SELL", D(2025, 9, 10), 100, 20m), // FY2026: 1000 discount gain
        };
        var match = CgtParcels.Match(trades);

        var fy = CgtDiscountService.Build(match, Individual, FinancialYear.Resolve(2026, null, null, D(2026, 10, 15)));
        Assert.Single(fy.Events);
        Assert.Equal(300m, fy.Summary.PriorYearLosses);
        Assert.Equal(350m, fy.Summary.NetCapitalGain); // (1000 - 300) / 2

        var custom = CgtDiscountService.Build(match, Individual, FinancialYear.Resolve(null, D(2025, 7, 1), D(2026, 6, 30), D(2026, 10, 15)));
        Assert.False(custom.PriorYearLossesIncluded);
        Assert.Equal(0m, custom.Summary.PriorYearLosses);
        Assert.Equal(500m, custom.Summary.NetCapitalGain);
    }
}

public class CgtParcelsTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    [Fact]
    public void One_sale_across_two_parcels_is_split_by_purchase_date()
    {
        var m = CgtParcels.Match(
        [
            new TradeLot(1, "ASX", "VAS", "BUY", D(2023, 7, 1), 100, 10m),
            new TradeLot(2, "ASX", "VAS", "BUY", D(2025, 1, 1), 100, 12m),
            new TradeLot(3, "ASX", "VAS", "SELL", D(2025, 3, 1), 150, 15m),
        ]);

        Assert.Equal(2, m.Events.Count);
        var first = m.Events.Single(e => e.Acquired == D(2023, 7, 1));
        Assert.Equal(100m, first.Units);
        Assert.Equal(1000m, first.CostBase);
        Assert.Equal(1500m, first.Proceeds);
        Assert.True(first.DiscountEligible);

        var second = m.Events.Single(e => e.Acquired == D(2025, 1, 1));
        Assert.Equal(50m, second.Units);
        Assert.Equal(150m, second.Gain);
        Assert.False(second.DiscountEligible);

        var open = Assert.Single(m.Open);
        Assert.Equal(50m, open.Units);
        Assert.Equal(12m, open.CostPerUnit);
        Assert.Equal(600m, open.TotalCost);
        Assert.Equal(D(2026, 1, 2), open.EligibleFrom);
        Assert.False(open.QualifiesOn(D(2026, 1, 1)));
        Assert.True(open.QualifiesOn(D(2026, 1, 2)));
        Assert.Empty(m.Problems);
    }

    [Fact]
    public void Earliest_purchase_is_used_first_even_when_entered_later()
    {
        var m = CgtParcels.Match(
        [
            new TradeLot(9, "ASX", "VAS", "BUY", D(2023, 1, 1), 10, 5m),
            new TradeLot(2, "ASX", "VAS", "BUY", D(2024, 1, 1), 10, 8m),
            new TradeLot(10, "ASX", "VAS", "SELL", D(2024, 6, 1), 10, 9m),
        ]);
        Assert.Equal(D(2023, 1, 1), Assert.Single(m.Events).Acquired);
        Assert.Equal(D(2024, 1, 1), Assert.Single(m.Open).Acquired);
    }

    [Fact]
    public void Holdings_are_matched_separately_ignoring_case_and_spaces()
    {
        var m = CgtParcels.Match(
        [
            new TradeLot(1, "ASX", "ABC", "BUY", D(2024, 1, 1), 10, 1m),
            new TradeLot(2, "NYSE", "ABC", "BUY", D(2024, 1, 1), 10, 100m),
            new TradeLot(3, "asx ", "abc", "sell", D(2024, 2, 1), 10, 2m),
        ]);
        var e = Assert.Single(m.Events);
        Assert.Equal(10m, e.Gain);
        Assert.Equal("NYSE", Assert.Single(m.Open).ExchangeCode);
    }

    [Fact]
    public void Negative_sell_quantities_are_treated_as_sales()
    {
        var m = CgtParcels.Match(
        [
            new TradeLot(1, "ASX", "ABC", "BUY", D(2024, 1, 1), 10, 1m),
            new TradeLot(2, "ASX", "ABC", "SELL", D(2024, 2, 1), -4, 2m),
        ]);
        Assert.Equal(4m, Assert.Single(m.Events).Units);
        Assert.Equal(6m, Assert.Single(m.Open).Units);
    }

    [Fact]
    public void Same_day_buy_and_sell_are_matched()
    {
        var m = CgtParcels.Match(
        [
            new TradeLot(2, "ASX", "ABC", "SELL", D(2024, 2, 1), 10, 2m),
            new TradeLot(5, "ASX", "ABC", "BUY", D(2024, 2, 1), 10, 1m),
        ]);
        var e = Assert.Single(m.Events);
        Assert.False(e.DiscountEligible);
        Assert.Empty(m.Problems);
    }

    [Fact]
    public void Selling_more_than_held_is_reported()
    {
        var m = CgtParcels.Match(
        [
            new TradeLot(1, "ASX", "ABC", "BUY", D(2024, 1, 1), 10, 1m),
            new TradeLot(2, "ASX", "ABC", "SELL", D(2024, 2, 1), 15, 2m),
        ]);
        Assert.Equal(10m, Assert.Single(m.Events).Units);
        Assert.Empty(m.Open);
        var p = Assert.Single(m.Problems);
        Assert.Equal(D(2024, 2, 1), p.Date);
        Assert.Contains("5 ABC units", p.Message);
    }

    [Fact]
    public void Unknown_trade_types_are_left_out_and_reported()
    {
        var m = CgtParcels.Match(
        [
            new TradeLot(1, "ASX", "ABC", "BUY", D(2024, 1, 1), 10, 1m),
            new TradeLot(2, "ASX", "ABC", "SPLIT", D(2024, 2, 1), 10, 0m),
        ]);
        Assert.Empty(m.Events);
        Assert.Equal(10m, Assert.Single(m.Open).Units);
        Assert.Contains("SPLIT", Assert.Single(m.Problems).Message);
    }

    [Fact]
    public void Trades_after_the_date_are_ignored()
    {
        var trades = new[]
        {
            new TradeLot(1, "ASX", "ABC", "BUY", D(2024, 1, 1), 10, 1m),
            new TradeLot(2, "ASX", "ABC", "SELL", D(2024, 6, 1), 10, 2m),
        };
        var before = CgtParcels.Match(trades, D(2024, 5, 31));
        Assert.Empty(before.Events);
        Assert.Equal(10m, Assert.Single(before.Open).Units);

        var after = CgtParcels.Match(trades, D(2024, 6, 1));
        Assert.Single(after.Events);
        Assert.Empty(after.Open);
    }

    [Fact]
    public void Amounts_are_rounded_to_cents()
    {
        var m = CgtParcels.Match(
        [
            new TradeLot(1, "ASX", "ABC", "BUY", D(2024, 1, 1), 3, 1.00333m),
            new TradeLot(2, "ASX", "ABC", "SELL", D(2024, 2, 1), 3, 2.00555m),
        ]);
        var e = Assert.Single(m.Events);
        Assert.Equal(3.01m, e.CostBase);
        Assert.Equal(6.02m, e.Proceeds);
    }
}
