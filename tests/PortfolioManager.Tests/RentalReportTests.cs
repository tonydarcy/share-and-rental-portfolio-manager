using PortfolioManager.Core.Debt;
using PortfolioManager.Core.Tax;
using PortfolioManager.Core.TaxWork;
using PortfolioManager.Web.Pages.TaxWork;

namespace PortfolioManager.Tests;

public class RentalReportTests
{
    private static readonly RentalProperty House = new(1, "1 Test St", new DateOnly(2020, 1, 1), 100, true, null);

    private static RentalPropertyYear Year(Dictionary<string, decimal> amounts, RentalProperty? property = null, Dictionary<string, decimal>? linked = null,
        decimal? loanInterest = null, IReadOnlyList<AssetYear>? assets = null, decimal balancing = 0) =>
        new(property ?? House, 2026, amounts, assets ?? [], linked, loanInterest, balancing);

    [Fact]
    public void Private_use_cuts_expenses_but_not_income()
    {
        // Available 146 of 365 days: 40% of the expenses
        var y = Year(new() { ["GrossRent"] = 10_000, ["Insurance"] = 1_000, ["Interest"] = 5_000, [RentalCategories.DaysAvailable] = 146, [RentalCategories.DaysPrivate] = 30 });
        Assert.Equal(0.4m, y.PrivateUseFactor);
        Assert.Equal(10_000m, y.GrossRent);
        Assert.Equal(2_000m, y.Interest);
        Assert.Equal(400m, y.OtherDeductions);
    }

    [Fact]
    public void No_days_entered_means_no_private_use()
    {
        Assert.Equal(1m, RentalPrivateUse.Factor(new Dictionary<string, decimal> { ["GrossRent"] = 1 }, 2026));
        Assert.Equal(0.5m, RentalPrivateUse.Factor(new Dictionary<string, decimal> { [RentalCategories.DaysAvailable] = 100, [RentalCategories.DaysOwned] = 200 }, 2026));
    }

    [Fact]
    public void Linked_amounts_add_to_what_was_keyed_and_tenant_bills_net_off()
    {
        var y = Year(new() { ["GrossRent"] = 1_000, ["Water"] = 800 }, linked: new() { ["GrossRent"] = 20_000, ["Water"] = -300, ["Repairs"] = 450 });
        Assert.Equal(21_000m, y.Amount("GrossRent"));
        Assert.Equal(500m, y.Amount("Water"));
        Assert.Equal(21_000m, y.GrossRent);
        Assert.Equal(950m, y.OtherDeductions);
    }

    [Fact]
    public void Linked_loans_replace_the_interest_entered()
    {
        var y = Year(new() { ["GrossRent"] = 20_000, ["Interest"] = 9_999 }, linked: new() { ["Interest"] = 1_234 }, loanInterest: 12_000);
        Assert.Equal(12_000m, y.Interest);
        Assert.Equal(12_000m, y.Lines().Single(l => l.Name == "Interest on loans").Whole);
    }

    [Fact]
    public void Travel_is_kept_but_not_deducted()
    {
        var y = Year(new() { ["GrossRent"] = 20_000, [RentalCategories.Travel] = 600 });
        Assert.Equal(0m, y.OtherDeductions);
        Assert.Equal(RentalLabel.None, y.Lines().Single(l => l.Name == "Travel").Label);
    }

    [Fact]
    public void Owners_each_get_their_share()
    {
        var y = Year(new() { ["GrossRent"] = 20_000, ["Insurance"] = 1_000 }, House with { OwnershipPct = 60 });
        Assert.Equal(12_000m, y.GrossRent);
        Assert.Equal(8_000m, y.ForShare(40).GrossRent);
        Assert.Equal(400m, y.ForShare(40).OtherDeductions);
    }

    [Fact]
    public void Low_value_pool_is_18_75_percent_then_37_5()
    {
        var pooled = new RentalAsset(1, 1, RentalAssetKinds.Plant, "Blinds", new DateOnly(2025, 3, 1), 800, null, RentalAssetKinds.LowValuePool, null, null);
        Assert.Equal(150m, RentalDepreciation.For(pooled, 2025).Deduction);
        Assert.Equal(650m, RentalDepreciation.For(pooled, 2026).Opening);
        Assert.Equal(243.75m, RentalDepreciation.For(pooled, 2026).Deduction);
        Assert.True(pooled.IsPooled);
    }

    [Fact]
    public void Balancing_adjustment_is_termination_value_less_written_down_value()
    {
        var oven = new RentalAsset(1, 1, RentalAssetKinds.Plant, "Oven", new DateOnly(2024, 7, 1), 1_000, 10, RentalAssetKinds.PrimeCost, null,
            new DateOnly(2025, 6, 30), TerminationValue: 950);
        // Prime cost 10 years: 100 off in 2024-25, so 900 left; sold for 950
        Assert.Equal(50m, RentalDepreciation.BalancingAdjustment(oven, 2025));
        Assert.Equal(0m, RentalDepreciation.BalancingAdjustment(oven, 2026));
        Assert.Equal(-900m, RentalDepreciation.BalancingAdjustment(oven with { TerminationValue = null }, 2025));
    }

    [Fact]
    public void Balancing_adjustments_go_to_other_income_or_capital_allowances()
    {
        var gain = Year(new() { ["GrossRent"] = 10_000 }, balancing: 50);
        Assert.Equal(10_050m, gain.GrossRent);
        var loss = Year(new() { ["GrossRent"] = 10_000 }, balancing: -900);
        Assert.Equal(900m, loss.OtherDeductions);
    }

    [Fact]
    public void Claimed_to_is_nil_before_the_first_year()
    {
        var building = new RentalAsset(2, 1, RentalAssetKinds.Building, "Building", new DateOnly(2024, 7, 1), 400_000, null, null, 2.5m, null);
        Assert.Equal(0m, RentalDepreciation.ClaimedTo(building, 2024));
        Assert.Equal(20_000m, RentalDepreciation.ClaimedTo(building, 2026));
    }

    private static RentalSale Sale(DateOnly acquired, DateOnly contract, decimal proceeds, decimal price, int mainResidence = 0, int absence = 0,
        DateOnly? firstRented = null, decimal? marketValue = null) =>
        new(1, acquired, contract, proceeds, price, 20_000, 0, 10_000, 0, mainResidence, absence, firstRented, marketValue, null);

    [Fact]
    public void Sale_cost_base_less_capital_works_claimed()
    {
        var building = new RentalAsset(2, 1, RentalAssetKinds.Building, "Building", new DateOnly(2020, 7, 1), 200_000, null, null, 2.5m, null);
        var r = RentalSaleCalc.Work(Sale(new DateOnly(2020, 7, 1), new DateOnly(2025, 6, 30), 800_000, 500_000), 50, [building]);
        Assert.Equal(530_000m, r.CostBase);
        Assert.Equal(25_000m, r.CapitalWorksClaimed);
        Assert.Equal(295_000m, r.WholeGain);
        Assert.Equal(147_500m, r.OwnerGain);
        Assert.True(r.DiscountEligible);
        var e = r.Event("1 Test St")!;
        Assert.True(e.IsPropertySale);
        Assert.Equal(400_000m, e.Proceeds);
    }

    [Fact]
    public void Main_residence_days_are_exempt_pro_rata()
    {
        var r = RentalSaleCalc.Work(Sale(new DateOnly(2021, 1, 1), new DateOnly(2024, 12, 31), 700_000, 500_000, mainResidence: 731), 100, []);
        Assert.Equal(1461, r.DaysOwned);
        Assert.Equal(0.499658m, r.TaxableShare);
    }

    [Fact]
    public void Six_year_absence_is_capped()
    {
        var r = RentalSaleCalc.Work(Sale(new DateOnly(2010, 1, 1), new DateOnly(2024, 12, 31), 900_000, 400_000, mainResidence: 1000, absence: 4000), 100, []);
        Assert.True(r.AbsenceCapped);
        Assert.Equal(1000 + RentalSaleCalc.AbsenceLimitDays, r.DaysExempt);
    }

    [Fact]
    public void Market_value_when_first_rented_replaces_the_purchase_price()
    {
        var r = RentalSaleCalc.Work(Sale(new DateOnly(2010, 1, 1), new DateOnly(2025, 1, 1), 900_000, 300_000, firstRented: new DateOnly(2018, 1, 1), marketValue: 650_000), 100, []);
        Assert.True(r.MarketValueRule);
        Assert.Equal(new DateOnly(2018, 1, 1), r.AcquiredForCgt);
        Assert.Equal(680_000m, r.CostBase);
        Assert.Equal(1m, r.TaxableShare);
    }

    [Fact]
    public void Pre_cgt_and_fully_exempt_sales_have_no_event()
    {
        Assert.Null(RentalSaleCalc.Work(Sale(new DateOnly(1980, 1, 1), new DateOnly(2025, 1, 1), 900_000, 50_000), 100, []).Event("x"));
        var home = RentalSaleCalc.Work(Sale(new DateOnly(2020, 1, 1), new DateOnly(2025, 1, 1), 900_000, 500_000, mainResidence: 5000), 100, []);
        Assert.Equal(0m, home.TaxableShare);
        Assert.Null(home.Event("x"));
    }

    [Fact]
    public void Review_flags_interest_without_rent_large_repairs_and_missing_statements()
    {
        var p = House with { PayoutCode = "1TEST", FirstEarned = new DateOnly(2025, 5, 1) };
        var y = Year(new() { ["Interest"] = 5_000, [RentalCategories.Travel] = 100 }, p);
        var statements = new[] { new DateOnly(2025, 7, 15), new DateOnly(2025, 9, 15) };
        var flags = RentalReview.Check(y, statements, [6_000m], new DateOnly(2025, 11, 10));
        Assert.Contains(flags, f => f.Text.StartsWith("Interest is claimed"));
        Assert.Contains(flags, f => f.Text.StartsWith("Repairs of"));
        Assert.Contains(flags, f => f.Text.StartsWith("Travel"));
        Assert.Contains(flags, f => f.Text == "No agent statement for August 2025.");
        Assert.Contains(flags, f => f.Text == "No agent statement for October 2025.");
        Assert.DoesNotContain(flags, f => f.Text.Contains("November"));
    }

    [Fact]
    public void Comparison_flags_big_changes_only()
    {
        Assert.True(RentalComparison.IsBig(4_000, 5_000));
        Assert.False(RentalComparison.IsBig(10_000, 11_000));
        Assert.False(RentalComparison.IsBig(100, 900));
        Assert.True(RentalComparison.IsBig(0, 1_500));
        var lines = RentalComparison.Compare(Year(new() { ["GrossRent"] = 20_000 }), Year(new() { ["GrossRent"] = 20_500, ["Repairs"] = 3_000 }));
        Assert.True(lines.Single(l => l.Name == "Repairs and maintenance").Flagged);
        Assert.False(lines.Single(l => l.Name == "Gross rent").Flagged);
        Assert.Equal("Net rent (whole property)", lines[^1].Name);
    }

    [Fact]
    public void Statement_groups_dated_amounts_by_month_with_the_keyed_figures()
    {
        var from = new DateOnly(2025, 7, 1);
        var dated = new[]
        {
            new RentalDatedAmount(1, new DateOnly(2025, 7, 20), "GrossRent", 2_000, "Agent statement"),
            new RentalDatedAmount(1, new DateOnly(2025, 7, 25), "GrossRent", 100, "Rent ledger"),
            new RentalDatedAmount(1, new DateOnly(2025, 8, 20), "Repairs", 300, "Maintenance log"),
            new RentalDatedAmount(1, new DateOnly(2026, 8, 1), "GrossRent", 9_999, "Agent statement"),
        };
        var s = RentalStatements.Build(from, new DateOnly(2026, 6, 30), dated, new Dictionary<string, decimal> { ["Insurance"] = 1_200 });
        Assert.Equal(12, s.Months.Count);
        Assert.Equal(2_100m, s.MonthNet(from));
        Assert.Equal(-300m, s.MonthNet(new DateOnly(2025, 8, 1)));
        Assert.Equal(-1_200m, s.KeyedNet);
        Assert.Equal(600m, s.Net);
    }

    [Fact]
    public void Payout_columns_map_to_schedule_categories()
    {
        Assert.Equal(RentalCategories.GrossRent, RentalPayoutMap.CategoryFor("dblRentalIncomeAmount"));
        Assert.Equal(RentalCategories.Repairs, RentalPayoutMap.CategoryFor("dblRepairsAndMaintenanceExpenseAmount"));
        Assert.Equal(RentalCategories.Sundry, RentalPayoutMap.CategoryFor("dblSundryRentalExpense1Amount"));
        // Every mapped category is a real schedule category
        Assert.All(RentalPayoutMap.Categories.Values, c => Assert.NotNull(RentalCategories.Find(c)));
        Assert.All(RentalPayoutMap.Categories.Keys, k => Assert.Contains(k, RentalPayoutMap.Columns));
    }

    [Fact]
    public void Yields_and_equity()
    {
        var y = new RentalYield(House with { OwnershipPct = 50 }, 800_000, 300_000, 40_000, 8_000);
        Assert.Equal(100_000m, y.Equity);
        Assert.Equal(0.75m, y.LoanToValue);
        Assert.Equal(0.05m, y.GrossYield);
        Assert.Equal(0.04m, y.NetYield);
        Assert.Null((y with { Value = null }).GrossYield);
    }

    [Fact]
    public void Days_must_fit_in_the_year()
    {
        Assert.Null(RentalModel.DaysError(new Dictionary<string, decimal> { ["Weeks"] = 52, [RentalCategories.DaysAvailable] = 300, [RentalCategories.DaysPrivate] = 60 }));
        Assert.NotNull(RentalModel.DaysError(new Dictionary<string, decimal> { ["Weeks"] = 54 }));
        Assert.NotNull(RentalModel.DaysError(new Dictionary<string, decimal> { [RentalCategories.DaysAvailable] = 367 }));
        Assert.NotNull(RentalModel.DaysError(new Dictionary<string, decimal> { [RentalCategories.DaysAvailable] = 10.5m }));
        Assert.NotNull(RentalModel.DaysError(new Dictionary<string, decimal> { [RentalCategories.DaysAvailable] = 100, [RentalCategories.DaysPrivate] = 50, [RentalCategories.DaysOwned] = 120 }));
    }

    [Fact]
    public void Private_redraws_reduce_the_deductible_interest()
    {
        var loan = new Loan(1, "Investment loan", null, LoanKinds.Investment, 6, 100, null, new DateOnly(2024, 7, 1), null);
        var entries = new[]
        {
            new LoanEntry(1, 1, new DateOnly(2024, 7, 1), LoanEntryKinds.Drawdown, 400_000, null),
            new LoanEntry(2, 1, new DateOnly(2025, 1, 1), LoanEntryKinds.Redraw, 100_000, "Car"),
            new LoanEntry(3, 1, new DateOnly(2025, 6, 30), LoanEntryKinds.Interest, 25_000, null),
        };
        var y = LoanCalc.Year(loan, entries, new DateOnly(2024, 7, 1), new DateOnly(2025, 6, 30));
        Assert.Equal(525_000m, y.ClosingBalance);
        Assert.Equal(100_000m, y.PrivateRedrawn);
        // (525,000 - 100,000) / 525,000 of the interest
        Assert.Equal(20_238.10m, y.Deductible);
    }
}
