using PortfolioManager.Core.CorporateActions;
using PortfolioManager.Core.Data;
using PortfolioManager.Core.Payouts;
using PortfolioManager.Core.Tax;

namespace PortfolioManager.Tests;

public class ShareSplitTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    private static readonly SplitRatio TwoForOne = new(2, 1);

    [Fact]
    public void Adjust_changes_units_and_unit_cost_but_not_the_total()
    {
        var t = ShareSplit.Adjust(new TradeLot(1, "ASX", "XYZ", "Buy", D(2022, 7, 1), 1000, 20.02m), TwoForOne);
        Assert.Equal(2000m, t.Quantity);
        Assert.Equal(10.01m, t.CostBasisPerUnit);
        Assert.Equal(20020m, t.Quantity * t.CostBasisPerUnit);
        Assert.Equal(D(2022, 7, 1), t.Date);
    }

    [Fact]
    public void Only_the_holdings_trades_before_the_effective_date_change()
    {
        var trades = new[]
        {
            new TradeLot(1, "ASX", "XYZ", "Buy", D(2024, 1, 1), 100, 10m),
            new TradeLot(2, "ASX", "XYZ", "Buy", D(2024, 3, 1), 100, 6m),  // on the effective date: already new units
            new TradeLot(3, "ASX", "ABC", "Buy", D(2024, 1, 1), 100, 10m),
            new TradeLot(4, "NYSE", "XYZ", "Buy", D(2024, 1, 1), 100, 10m),
        };
        var after = ShareSplit.Apply(trades, "asx", "xyz ", D(2024, 3, 1), TwoForOne);
        Assert.Equal([200m, 100m, 100m, 100m], after.Select(t => t.Quantity));
        Assert.Equal([5m, 6m, 10m, 10m], after.Select(t => t.CostBasisPerUnit));
    }

    [Fact]
    public void Split_after_a_partial_sale_keeps_gains_cost_base_and_purchase_date()
    {
        var trades = new[]
        {
            new TradeLot(1, "ASX", "XYZ", "Buy", D(2023, 1, 10), 100, 10m),
            new TradeLot(2, "ASX", "XYZ", "Sell", D(2023, 6, 1), 40, 15m),
        };
        // 2 for 1 from 1 Jan 2024, then 60 of the 120 new units sold
        var adjusted = ShareSplit.Apply(trades, "ASX", "XYZ", D(2024, 1, 1), TwoForOne)
            .Append(new TradeLot(3, "ASX", "XYZ", "Sell", D(2024, 3, 1), 60, 9m))
            .ToList();

        var m = CgtParcels.Match(adjusted);
        Assert.Equal(2, m.Events.Count);

        var first = m.Events[0];
        Assert.Equal(80m, first.Units); // the 40 sold, in today's units
        Assert.Equal(400m, first.CostBase);
        Assert.Equal(600m, first.Proceeds);
        Assert.Equal(200m, first.Gain); // the same as before the split

        var second = m.Events[1];
        Assert.Equal(D(2023, 1, 10), second.Acquired);
        Assert.Equal(300m, second.CostBase);
        Assert.Equal(540m, second.Proceeds);
        Assert.True(second.DiscountEligible);

        var open = Assert.Single(m.Open);
        Assert.Equal(60m, open.Units);
        Assert.Equal(300m, open.TotalCost);
        Assert.Empty(m.Problems);
    }

    [Fact]
    public void Preview_shows_units_double_and_cost_base_unchanged()
    {
        var trades = new[]
        {
            new TradeLot(1, "ASX", "XYZ", "Buy", D(2022, 7, 1), 1000, 20.02m),
            new TradeLot(2, "ASX", "XYZ", "Sell", D(2023, 7, 1), 200, 25m),
        };
        var p = ShareSplit.Preview(trades, "ASX", "XYZ", D(2024, 3, 1), TwoForOne);
        Assert.Equal(2, p.TradesAdjusted);
        Assert.Equal(800m, p.UnitsBefore);
        Assert.Equal(1600m, p.UnitsAfter);
        Assert.Equal(16016m, p.CostBaseBefore);
        Assert.Equal(p.CostBaseBefore, p.CostBaseAfter);
        Assert.False(p.HasFractionalUnits);
    }

    [Fact]
    public void Consolidation_keeps_the_cost_base_and_flags_part_units()
    {
        var trades = new[] { new TradeLot(1, "ASX", "XYZ", "Buy", D(2024, 1, 1), 100, 10m) };
        var p = ShareSplit.Preview(trades, "ASX", "XYZ", D(2024, 6, 1), new SplitRatio(1, 3));
        Assert.Equal(100m, p.UnitsBefore);
        Assert.Equal(33.3333m, Math.Round(p.UnitsAfter, 4));
        Assert.Equal(1000m, p.CostBaseAfter);
        Assert.True(p.HasFractionalUnits);
    }

    [Fact]
    public void Nothing_to_adjust_when_every_trade_is_after_the_date()
    {
        var trades = new[] { new TradeLot(1, "ASX", "XYZ", "Buy", D(2024, 6, 1), 100, 10m) };
        Assert.Equal(0, ShareSplit.Preview(trades, "ASX", "XYZ", D(2024, 1, 1), TwoForOne).TradesAdjusted);
    }

    [Theory]
    [InlineData(2.0, 2, 1)]
    [InlineData(4.0, 4, 1)]
    [InlineData(0.5, 1, 2)]
    [InlineData(0.1, 1, 10)]
    [InlineData(1.5, 3, 2)]
    [InlineData(0.3333333333333333, 1, 3)]
    [InlineData(0.04, 1, 25)]
    public void Ratio_from_the_stored_factor(double factor, int newUnits, int oldUnits) =>
        Assert.Equal(new SplitRatio(newUnits, oldUnits), SplitRatio.FromFactor(factor));

    [Theory]
    [InlineData(0.0)]
    [InlineData(-2.0)]
    [InlineData(double.NaN)]
    public void No_ratio_for_a_bad_factor(double factor) => Assert.Null(SplitRatio.FromFactor(factor));

    [Fact]
    public void Ratio_text_and_kind()
    {
        Assert.Equal("2 for 1", TwoForOne.ToString());
        Assert.Equal("Split", TwoForOne.Kind);
        Assert.Equal("Consolidation", new SplitRatio(1, 10).Kind);
        Assert.Equal(0.1m, new SplitRatio(1, 10).Factor);
    }

    private static SplitForm Form(int? newUnits = 2, int? oldUnits = 1, DateOnly? date = null) =>
        new() { Market = " asx ", Code = "xyz", Date = date ?? D(2024, 3, 1), NewUnits = newUnits, OldUnits = oldUnits };

    [Fact]
    public void Valid_form_is_cleaned_and_reduced()
    {
        var r = ShareSplit.Validate(Form(4, 2), D(2026, 10, 15));
        Assert.True(r.IsValid);
        Assert.Equal("ASX", r.Value!.Market);
        Assert.Equal("XYZ", r.Value.Code);
        Assert.Equal(TwoForOne, r.Value.Ratio);
    }

    [Theory]
    [InlineData(1, 1, "NewUnits")]
    [InlineData(0, 1, "NewUnits")]
    [InlineData(2, null, "OldUnits")]
    [InlineData(2, 2_000_000, "OldUnits")]
    public void Bad_ratios_are_refused(int? newUnits, int? oldUnits, string field) =>
        Assert.Contains(ShareSplit.Validate(Form(newUnits, oldUnits), D(2026, 10, 15)).Errors, e => e.Field == field);

    [Fact]
    public void Future_and_missing_fields_are_refused()
    {
        Assert.Contains(ShareSplit.Validate(Form(date: D(2026, 10, 16)), D(2026, 10, 15)).Errors, e => e.Field == "Date");
        var r = ShareSplit.Validate(new SplitForm(), D(2026, 10, 15));
        Assert.Equal(["Market", "Code", "Date", "NewUnits", "OldUnits"], r.Errors.Select(e => e.Field));
    }
}

public class CapitalReturnTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    [Fact]
    public void Shared_by_units_and_adds_up_to_the_cent()
    {
        var s = CapitalReturns.Apportion([(1m, 50m), (1m, 50m), (1m, 50m)], 100m);
        Assert.Equal([33.33m, 33.33m, 33.34m], s.Select(x => x.Share));
        Assert.Equal(100m, s.Sum(x => x.Share));
        Assert.All(s, x => Assert.Equal(0m, x.Excess));
    }

    [Fact]
    public void More_than_the_cost_base_is_excess()
    {
        var s = CapitalReturns.Apportion([(100m, 50m), (100m, 500m)], 200m);
        Assert.Equal(50m, s[0].Reduction);
        Assert.Equal(50m, s[0].Excess);
        Assert.Equal(0m, s[0].CostBaseAfter);
        Assert.Equal(100m, s[1].Reduction);
        Assert.Equal(0m, s[1].Excess);
        Assert.Equal(400m, s[1].CostBaseAfter);
    }

    [Fact]
    public void Nothing_to_share_without_units() =>
        Assert.Empty(CapitalReturns.Apportion([], 100m));

    [Fact]
    public void Units_on_the_day_undo_later_splits()
    {
        Assert.Equal(1000m, CapitalReturns.UnitsOnTheDay(2000m, [2m]));
        Assert.Equal(1000m, CapitalReturns.UnitsOnTheDay(1000m, []));
        Assert.Equal(400m, CapitalReturns.UnitsOnTheDay(100m, [0.5m, 0.5m]));
        Assert.Equal(120.50m, CapitalReturns.TotalFromPerUnit(0.241m, 500m));
    }

    [Fact]
    public void Reduces_the_cost_base_of_parcels_held_pro_rata()
    {
        var m = CgtParcels.Match(
        [
            new TradeLot(1, "ASX", "XYZ", "Buy", D(2023, 1, 1), 100, 10m),
            new TradeLot(2, "ASX", "XYZ", "Buy", D(2023, 6, 1), 300, 12m),
        ], null, [new CapitalReturn("ASX", "XYZ", D(2024, 1, 1), 200m)]);

        Assert.Empty(m.Events);
        Assert.Equal(1, m.CapitalReturns);
        Assert.Equal(950m, m.Open[0].TotalCost);  // 1,000 less 50
        Assert.Equal(3450m, m.Open[1].TotalCost); // 3,600 less 150
        Assert.Equal(D(2023, 1, 1), m.Open[0].Acquired);
    }

    [Fact]
    public void Only_units_held_on_the_day_count_and_sales_after_use_the_reduced_cost_base()
    {
        var m = CgtParcels.Match(
        [
            new TradeLot(1, "ASX", "XYZ", "Buy", D(2020, 1, 1), 100, 10m),
            new TradeLot(2, "ASX", "XYZ", "Sell", D(2021, 1, 1), 50, 12m),
            new TradeLot(3, "ASX", "XYZ", "Buy", D(2023, 1, 1), 50, 11m),
            new TradeLot(4, "ASX", "XYZ", "Sell", D(2024, 1, 1), 100, 13m),
        ], null, [new CapitalReturn("ASX", "XYZ", D(2022, 1, 1), 50m)]); // $1 a unit on the 50 held

        Assert.Equal(3, m.Events.Count);
        Assert.Equal(500m, m.Events[0].CostBase);  // sold before the return
        Assert.Equal(450m, m.Events[1].CostBase);  // 50 units from 2020: 500 less 50
        Assert.Equal(550m, m.Events[2].CostBase);  // bought after the return
        Assert.Empty(m.Open);
    }

    [Fact]
    public void Return_on_the_day_of_a_sale_goes_to_the_units_left()
    {
        var m = CgtParcels.Match(
        [
            new TradeLot(1, "ASX", "XYZ", "Buy", D(2023, 1, 1), 100, 10m),
            new TradeLot(2, "ASX", "XYZ", "Sell", D(2024, 1, 1), 60, 12m),
        ], null, [new CapitalReturn("ASX", "XYZ", D(2024, 1, 1), 40m)]);
        Assert.Equal(600m, Assert.Single(m.Events).CostBase);
        Assert.Equal(360m, Assert.Single(m.Open).TotalCost);
    }

    [Fact]
    public void Excess_over_the_cost_base_is_a_G1_gain_and_later_sales_have_nil_cost_base()
    {
        var m = CgtParcels.Match(
        [
            new TradeLot(1, "ASX", "XYZ", "Buy", D(2022, 1, 1), 100, 2m),
            new TradeLot(2, "ASX", "XYZ", "Sell", D(2025, 1, 1), 100, 1m),
        ], null, [new CapitalReturn("ASX", "XYZ", D(2024, 1, 1), 250m)]);

        Assert.Equal(2, m.Events.Count);
        var g1 = m.Events[0];
        Assert.Equal(CgtEventType.CapitalReturnExcess, g1.Type);
        Assert.True(g1.IsCapitalReturn);
        Assert.Equal(D(2024, 1, 1), g1.Disposed);
        Assert.Equal(D(2022, 1, 1), g1.Acquired);
        Assert.Equal(50m, g1.Gain);
        Assert.True(g1.DiscountEligible);

        var sale = m.Events[1];
        Assert.Equal(0m, sale.CostBase);
        Assert.Equal(100m, sale.Gain);
    }

    [Fact]
    public void Return_with_nothing_held_is_reported()
    {
        var m = CgtParcels.Match(
        [
            new TradeLot(1, "ASX", "XYZ", "Buy", D(2023, 1, 1), 100, 10m),
            new TradeLot(2, "ASX", "XYZ", "Sell", D(2023, 6, 1), 100, 12m),
        ], null,
        [
            new CapitalReturn("ASX", "XYZ", D(2024, 1, 1), 40m),
            new CapitalReturn("ASX", "QQQ", D(2024, 1, 1), 10m),
        ]);
        Assert.Equal(0, m.CapitalReturns);
        Assert.Equal(2, m.Problems.Count);
        Assert.All(m.Problems, p => Assert.Contains("no units were held", p.Message));
    }

    [Fact]
    public void Returns_after_the_as_at_date_are_ignored()
    {
        var m = CgtParcels.Match([new TradeLot(1, "ASX", "XYZ", "Buy", D(2023, 1, 1), 100, 10m)],
            D(2023, 12, 31), [new CapitalReturn("ASX", "XYZ", D(2024, 1, 1), 40m)]);
        Assert.Equal(1000m, Assert.Single(m.Open).TotalCost);
        Assert.Equal(0, m.CapitalReturns);
    }

    // The worked example: a 2 for 1 split, a second purchase, then a return of capital bigger than both cost bases

    private static readonly TradeLot[] ExampleTrades =
    [
        // 1,000 XYZ at $20 + $20 brokerage, then 2 for 1 from 1 Mar 2024 (as uspApplyStockSplit stores it)
        ShareSplit.Adjust(new TradeLot(1, "ASX", "XYZ", "Buy", new(2022, 7, 1), 1000, 20.02m), new SplitRatio(2, 1)),
        // 500 more at $9 + $10 brokerage
        new TradeLot(2, "ASX", "XYZ", "Buy", new(2025, 4, 1), 500, 9.02m),
    ];

    [Fact]
    public void Worked_example_split_then_return_of_capital_with_excess()
    {
        var r = new NewCapitalReturn("ASX", "XYZ", D(2025, 7, 1), 12m, null, null);
        var preview = ReturnOfCapitalEntry.Preview(ExampleTrades, [], r, []);
        Assert.Equal(2500m, preview.UnitsHeld);
        Assert.Equal(30000m, preview.Total);
        Assert.Equal([24000m, 6000m], preview.Parcels.Select(p => p.Share.Share));
        Assert.Equal([20020m, 4510m], preview.Parcels.Select(p => p.Share.CostBaseBefore));
        Assert.Equal([3980m, 1490m], preview.Parcels.Select(p => p.Share.Excess));
        Assert.Equal(5470m, preview.Excess);

        var match = CgtParcels.Match(ExampleTrades, null, [new CapitalReturn("ASX", "XYZ", D(2025, 7, 1), preview.Total)]);
        var report = CgtDiscountService.Build(match, CgtDiscount.RateFor("Individual"),
            FinancialYear.Resolve(2026, null, null, D(2026, 10, 15)), CgtParcels.Match(ExampleTrades));
        Assert.Equal(3980m, report.Summary.DiscountGains); // the 2022 parcel, held over 12 months
        Assert.Equal(1490m, report.Summary.OtherGains);    // the April 2025 parcel
        Assert.Equal(1990m, report.Summary.Discount);
        Assert.Equal(3480m, report.Summary.NetCapitalGain);
        Assert.Equal(5470m, report.CapitalReturnGains);
        Assert.Equal(1, report.CapitalReturns);
        Assert.True(report.AgreesWith(0m)); // the legacy report has no sales to compare
        Assert.All(match.Open, p => Assert.Equal(0m, p.TotalCost));
        Assert.Equal(2500m, match.Open.Sum(p => p.Units));
    }

    [Fact]
    public void Per_unit_amount_after_a_later_split_uses_that_days_units()
    {
        var trades = new[] { ShareSplit.Adjust(new TradeLot(1, "ASX", "XYZ", "Buy", D(2020, 1, 1), 1000, 10m), new SplitRatio(2, 1)) };
        var r = new NewCapitalReturn("ASX", "XYZ", D(2021, 1, 1), 0.50m, null, null);
        var p = ReturnOfCapitalEntry.Preview(trades, [], r, [2m]);
        Assert.Equal(2000m, p.UnitsHeld);
        Assert.Equal(1000m, p.UnitsOnTheDay);
        Assert.Equal(500m, p.Total);
    }

    [Fact]
    public void Earlier_returns_are_taken_off_before_the_preview()
    {
        var trades = new[] { new TradeLot(1, "ASX", "XYZ", "Buy", D(2020, 1, 1), 100, 10m) };
        var r = new NewCapitalReturn("ASX", "XYZ", D(2022, 1, 1), null, 300m, null);
        var p = ReturnOfCapitalEntry.Preview(trades, [new CapitalReturn("ASX", "XYZ", D(2021, 1, 1), 800m)], r, []);
        Assert.Equal(200m, p.CostBaseBefore);
        Assert.Equal(200m, p.Reduction);
        Assert.Equal(100m, p.Excess);
    }

    [Fact]
    public void Comparison_with_the_legacy_report_leaves_out_returns_of_capital()
    {
        var trades = new[]
        {
            new TradeLot(1, "ASX", "XYZ", "Buy", D(2025, 8, 1), 100, 10m),
            new TradeLot(2, "ASX", "XYZ", "Sell", D(2026, 3, 1), 100, 12m),
        };
        var returns = new[] { new CapitalReturn("ASX", "XYZ", D(2025, 12, 1), 100m) };
        var period = FinancialYear.Resolve(2026, null, null, D(2026, 10, 15));
        var report = CgtDiscountService.Build(CgtParcels.Match(trades, null, returns), CgtDiscount.RateFor("Individual"),
            period, CgtParcels.Match(trades));
        Assert.Equal(300m, report.TotalGain);
        Assert.Equal(200m, report.ComparableGain);
        Assert.True(report.AgreesWith(200m));
        Assert.False(report.AgreesWith(300m));
    }

    private static CapitalReturnForm Form(decimal? perUnit = null, decimal? total = 100m) =>
        new() { Market = "asx", Code = " xyz", Date = D(2025, 7, 1), PerUnit = perUnit, Total = total };

    [Fact]
    public void Form_takes_per_unit_or_total()
    {
        var v = ReturnOfCapitalEntry.Validate(Form(), D(2026, 10, 15));
        Assert.True(v.IsValid);
        Assert.Equal("XYZ", v.Value!.Code);
        Assert.True(ReturnOfCapitalEntry.Validate(Form(0.25m, null), D(2026, 10, 15)).IsValid);
    }

    [Theory]
    [InlineData(null, null, "PerUnit")]
    [InlineData(0.25, 100.0, "PerUnit")]
    [InlineData(-1.0, null, "PerUnit")]
    [InlineData(null, 0.0, "Total")]
    [InlineData(null, 10.005, "Total")]
    public void Form_refuses_bad_amounts(double? perUnit, double? total, string field) =>
        Assert.Contains(ReturnOfCapitalEntry.Validate(Form((decimal?)perUnit, (decimal?)total), D(2026, 10, 15)).Errors, e => e.Field == field);

    [Fact]
    public void Form_refuses_a_future_date()
    {
        var f = Form();
        f.Date = D(2026, 10, 17);
        Assert.Contains(ReturnOfCapitalEntry.Validate(f, D(2026, 10, 15)).Errors, e => e.Field == "Date");
    }
}

public class DrpInputTests
{
    private static DrpInput Drp(int units = 30, decimal price = 33.10m) => new()
    {
        StockCode = "BHP",
        Date = new DateTime(2026, 3, 26),
        Franked = 1000m,
        FrankingCredit = 428.57m,
        Units = units,
        Price = price,
    };

    [Fact]
    public void Works_out_cash_cost_and_residual()
    {
        var d = Drp();
        Assert.Equal(1000m, d.Cash);
        Assert.Equal(993m, d.Reinvested);
        Assert.Equal(7m, d.Residual);
        Assert.Empty(d.Validate());
    }

    [Fact]
    public void Tax_withheld_comes_off_the_cash()
    {
        var d = Drp(units: 16);
        d.Franked = 0;
        d.Unfranked = 1000m;
        d.TaxWithheld = 470m;
        Assert.Equal(530m, d.Cash);
        Assert.Equal(0.40m, d.Residual);
        Assert.Empty(d.Validate());
    }

    [Fact]
    public void A_carried_forward_balance_can_make_up_part_of_a_unit() =>
        Assert.Empty(Drp(units: 31).Validate()); // 1,026.10 against 1,000

    [Theory]
    [InlineData(29)] // leaves more than a unit's price
    [InlineData(32)]
    public void Units_must_match_the_dividend(int units) =>
        Assert.Contains(Drp(units: units).Validate(), e => e.Contains("less than one unit"));

    [Fact]
    public void Missing_fields_are_refused()
    {
        var errors = new DrpInput().Validate();
        Assert.Contains(errors, e => e.Contains("stock code"));
        Assert.Contains(errors, e => e.Contains("dividend"));
        Assert.Contains(errors, e => e.Contains("units"));
        Assert.Contains(errors, e => e.Contains("DRP price"));
    }

    [Fact]
    public void Negative_amounts_are_refused() =>
        Assert.Contains("Amounts can't be negative.", new DrpInput { StockCode = "BHP", Franked = -1, Units = 1, Price = 1 }.Validate());
}

public class ProcResultTests
{
    [Fact]
    public void Reads_success_and_errors()
    {
        Assert.True(ProcResult.From("Split Applied Successfully", "Split Applied", 7) is { Ok: true, Id: 7 });
        Assert.Equal("Not found.", ProcResult.From("Error: Not found.", "Split Applied").Error);
        Assert.False(ProcResult.From(null, "Split Applied").Ok);
        Assert.False(ProcResult.From("Something else", "Split Applied").Ok);
        Assert.True(ProcResult.From("Payout Deleted Sucessfully", "Payout Deleted").Ok);
    }
}
