using PortfolioManager.Core.Tax;
using PortfolioManager.Core.TaxWork;

namespace PortfolioManager.Tests;

public class TaxPlanningTests
{
    [Theory]
    [InlineData(18_200, 0)]
    [InlineData(45_000, 4_288)]
    [InlineData(135_000, 31_288)]
    [InlineData(190_000, 51_638)]
    [InlineData(200_000, 56_138)]
    public void Resident_rates_2025_26(decimal taxable, decimal tax) => Assert.Equal(tax, IndividualRates.For(2026).Tax(taxable));

    [Fact]
    public void Bottom_rate_falls_to_15_then_14_percent()
    {
        Assert.Equal(4_020m, IndividualRates.For(2027).Tax(45_000));
        Assert.Equal(3_752m, IndividualRates.For(2028).Tax(45_000));
    }

    [Theory]
    [InlineData(30_000, 700)]
    [InlineData(40_000, 575)]
    [InlineData(50_000, 250)]
    [InlineData(70_000, 0)]
    public void Low_income_offset(decimal taxable, decimal lito) => Assert.Equal(lito, IndividualRates.LowIncomeOffset(taxable));

    [Fact]
    public void Franking_credits_and_withholding_reduce_what_is_payable()
    {
        var p = new PlanInputs(80_000, 10_000, 0, 0, 0, 0, 1_000, 15_000, 0);
        var r = TaxPlanner.Work(p, IndividualRates.For(2026));
        Assert.Equal(90_000m, r.TaxableIncome);
        Assert.Equal(17_788m, r.IncomeTax);
        Assert.Equal(1_800m, r.Medicare);
        Assert.Equal(3_588m, r.Payable);
        Assert.Equal(0.30m, r.MarginalRate);
    }

    [Fact]
    public void Super_contribution_is_taxed_in_the_fund_and_division_293_above_250k()
    {
        var rates = IndividualRates.For(2026);
        var low = TaxPlanner.Work(new PlanInputs(100_000, 0, 0, 0, 0, 10_000, 0, 0, 0), rates);
        Assert.Equal(1_500m, low.ContributionsTax);
        var high = TaxPlanner.Work(new PlanInputs(260_000, 0, 0, 0, 0, 10_000, 0, 0, 0), rates);
        Assert.True(high.Division293);
        Assert.Equal(3_000m, high.ContributionsTax);
    }

    private static CgtSummary Cgt(decimal discount, decimal other, decimal current, decimal prior, decimal net) =>
        new(new CgtDiscountRate(CgtEntityKind.Individual, 0.5m, false), discount, other, current, prior, 0, 0, 0, net, 0);

    [Fact]
    public void What_if_sale_goes_through_losses_then_the_discount()
    {
        var c = Cgt(10_000, 0, 0, 2_000, 4_000);
        Assert.Equal(4_000m, TaxPlanner.NetGainWith(c, null));
        Assert.Equal(6_500m, TaxPlanner.NetGainWith(c, new WhatIfSale(15_000, 10_000, true)));
        // Losses go against the non-discount gain first, so the whole discount gain keeps its discount
        Assert.Equal(8_000m, TaxPlanner.NetGainWith(c, new WhatIfSale(15_000, 10_000, false)));
        Assert.Equal(1_500m, TaxPlanner.NetGainWith(c, new WhatIfSale(5_000, 10_000, true)));
    }
}
