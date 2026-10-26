using PortfolioManager.Core.Tax;
using PortfolioManager.Core.TaxWork;

namespace PortfolioManager.Tests;

public class EssReductionTests
{
    [Theory]
    [InlineData(800, 150_000, 800)]
    [InlineData(2_500, 180_000, 1_000)]
    [InlineData(2_500, 180_001, 0)]
    [InlineData(0, 50_000, 0)]
    public void Up_to_1000_comes_off_when_the_income_test_is_met(decimal eligible, decimal incomeTest, decimal reduction) =>
        Assert.Equal(reduction, EssReduction.For(eligible, incomeTest));

    [Fact]
    public void The_income_test_adds_fringe_benefits_super_and_investment_losses() =>
        Assert.Equal(185_000m, EssReduction.IncomeTest(170_000, 5_000, 6_000, 4_000));

    [Fact]
    public void Tax_planning_takes_the_reduction_off_taxable_income()
    {
        var rates = IndividualRates.For(2026);
        var p = new PlanInputs(100_000, 0, 0, 0, 0, 0, 0, 0, 0, EssUpfrontEligible: 1_500, EssOtherDiscounts: 500);
        var r = TaxPlanner.Work(p, rates);
        Assert.Equal(1_000m, r.EssReduction);
        Assert.Equal(102_000m, r.EssIncomeTest);
        Assert.Equal(101_000m, r.TaxableIncome);

        var over = TaxPlanner.Work(p with { ReportableFringeBenefits = 80_000 }, rates);
        Assert.Equal(0m, over.EssReduction);
        Assert.Equal(102_000m, over.TaxableIncome);
    }

    [Fact]
    public void A_deductible_super_contribution_counts_in_the_income_test()
    {
        var p = new PlanInputs(185_000, 0, 0, 0, 0, 10_000, 0, 0, 0, EssUpfrontEligible: 1_000);
        // Taxable income before the reduction is 176,000, but the 10,000 contribution is reportable super
        Assert.Equal(0m, TaxPlanner.Work(p, IndividualRates.For(2026)).EssReduction);
    }
}

public class EssRegisterTests
{
    private static EssGrant Grant(string employee, string scheme, DateOnly acquired, decimal value, decimal paid,
        DateOnly? taxingPoint = null, decimal? taxingValue = null, bool tfn = true, decimal withheld = 0, bool foreign = false) =>
        new(0, employee, null, scheme, "Shares", acquired, 100, value, paid, null, taxingPoint, taxingValue, tfn, withheld, foreign, null);

    [Fact]
    public void Grants_go_in_the_year_acquired_or_the_year_of_the_deferred_taxing_point()
    {
        EssGrant[] grants =
        [
            Grant("Jo", EssSchemes.UpfrontEligible, new(2025, 9, 1), 2_000, 500),
            Grant("Jo", EssSchemes.Deferral, new(2023, 9, 1), 1_000, 0, new(2026, 2, 1), 3_000, foreign: true),
            Grant("Sam", EssSchemes.UpfrontNotEligible, new(2026, 6, 30), 900, 0),
            Grant("Sam", EssSchemes.Deferral, new(2024, 1, 1), 1_000, 0),
            Grant("Lee", EssSchemes.UpfrontEligible, new(2026, 7, 1), 1_000, 0),
        ];
        var r = EssRegisters.Build(grants, 2026);

        Assert.Equal(["Jo", "Sam"], r.Employees.Select(e => e.Employee));
        var jo = r.Employees[0];
        Assert.Equal((1_500m, 3_000m, 3_000m), (jo.UpfrontEligible, jo.Deferral, jo.ForeignSource));
        Assert.Equal(900m, r.Employees[1].UpfrontNotEligible);
        Assert.Equal(5_400m, r.TotalDiscount);
        Assert.Equal("Sam", Assert.Single(r.AwaitingTaxingPoint).Employee);
    }

    [Fact]
    public void Withholding_is_due_at_the_top_rate_when_no_tfn_was_quoted()
    {
        var g = Grant("Jo", EssSchemes.UpfrontEligible, new(2025, 9, 1), 2_000, 0, tfn: false, withheld: 500);
        Assert.Equal(940m, g.WithholdingDue);
        Assert.True(EssRegisters.Build([g], 2026).Employees[0].AnyWithholdingShort);
        Assert.Equal(0m, (g with { Scheme = EssSchemes.StartUp }).WithholdingDue);
    }

    [Fact]
    public void A_discount_is_never_negative() =>
        Assert.Equal(0m, Grant("Jo", EssSchemes.UpfrontEligible, new(2025, 9, 1), 100, 500).Discount);

    [Fact]
    public void Validation_refuses_a_tfn_as_the_reference_and_a_taxing_point_outside_a_deferral_scheme()
    {
        var g = Grant("Jo", EssSchemes.UpfrontEligible, new(2025, 9, 1), 100, 0, new(2026, 1, 1), 200) with { EmployeeRef = "123 456 782" };
        var errors = EssRegisters.Validate(g);
        Assert.Contains(errors, e => e.Contains("tax file number"));
        Assert.Contains(errors, e => e.Contains("Only a deferral scheme"));
        Assert.Empty(EssRegisters.Validate(Grant("Jo", EssSchemes.Deferral, new(2025, 9, 1), 100, 0, new(2026, 1, 1), 200)));
    }

    [Fact]
    public void The_csv_has_a_line_per_grant_with_the_return_label()
    {
        var csv = EssRegisters.ToCsv(EssRegisters.Build([Grant("Smith, Jo", EssSchemes.UpfrontEligible, new(2025, 9, 1), 2_000, 500)], 2026));
        var lines = csv.Trim().Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("2025-26,\"Smith, Jo\",,\"Taxed upfront, eligible for the $1,000 reduction\",12D,Shares,2025-09-01,100,2000,500,,,,1500,No,Yes,0", lines[1]);
    }

    [Fact]
    public void Parameters_carry_the_grant()
    {
        var p = EssGrantService.Parameters(7, Grant("Jo", EssSchemes.Deferral, new(2025, 9, 1), 1, 0, new(2026, 1, 1), 5));
        object? Get(string name) => p.GetType().GetProperty(name)!.GetValue(p);
        Assert.Equal((7, "Deferral", 5.0, (object)new DateTime(2026, 1, 1)), ((int)Get("iUserID")!, (string)Get("strScheme")!, (double)Get("dblTaxingPointValue")!, Get("dtmTaxingPoint")!));
    }
}
