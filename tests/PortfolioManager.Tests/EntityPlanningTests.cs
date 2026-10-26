using PortfolioManager.Core.TaxWork;

namespace PortfolioManager.Tests;

public class EntityPlanningTests
{
    private static TaxIncomeFacts Franking(decimal credits) => TaxIncomeFacts.None with { FrankingCredits = credits };

    [Fact]
    public void The_medicare_levy_shades_in_above_the_low_income_threshold()
    {
        var rates = IndividualRates.For(2025);
        Assert.Equal(0m, rates.Medicare(20_000));
        Assert.Equal(277.80m, rates.Medicare(30_000));       // (30,000 - 27,222) x 10%
        Assert.Equal(800m, rates.Medicare(40_000));          // the full 2%
        Assert.Equal(0m, rates.Medicare(40_000, senior: true));
        Assert.False(MedicareLowIncome.For(2027).Known);
    }

    [Fact]
    public void A_company_pays_tax_less_franking_and_instalments()
    {
        var r = EntityPlanner.Work(false, 25, 100_000, Franking(10_000), 5_000, 0, new EntityPlanChanges());
        Assert.Equal(25_000m, r.GrossTax);
        Assert.Equal(10_000m, r.Payable);
        Assert.Equal(45_000m, r.MaxFrankedDividend);

        var withDeduction = EntityPlanner.Work(false, 25, 100_000, Franking(10_000), 5_000, 0, new EntityPlanChanges(ExtraDeductions: 20_000));
        Assert.Equal(5_000m, withDeduction.Payable);
    }

    [Fact]
    public void A_company_cant_get_a_refund_of_franking_credits()
    {
        var r = EntityPlanner.Work(false, 25, 10_000, Franking(5_000), 0, 0, new EntityPlanChanges());
        Assert.Equal(2_500m, r.FrankingOffset);
        Assert.Equal(2_500m, r.ExcessFranking);
        Assert.Equal(0m, r.Payable);
    }

    [Fact]
    public void A_fund_takes_the_pension_share_out_of_extra_income_but_taxes_contributions_in_full()
    {
        var r = EntityPlanner.Work(true, 15, 100_000, Franking(30_000), 0, 40, new EntityPlanChanges(GainChange: 10_000, ExtraContributions: 20_000));
        Assert.Equal(126_000m, r.TaxableIncome);
        Assert.Equal(18_900m, r.GrossTax);
        Assert.Equal(-10_841m, r.Payable);   // 18,900 - 30,000 franking refunded + 259 levy
    }

    [Fact]
    public void A_company_beneficiary_is_taxed_on_the_gain_grossed_back_up()
    {
        var plan = TrustPlanner.Work(2026, 100_000, 20_000, 0, true,
            [new PlanBeneficiary("Pat", BeneficiaryTypes.Individual, 50, 0), new PlanBeneficiary("Bucket Co", BeneficiaryTypes.Company, 50, 0)]);
        var company = plan.Beneficiaries.Single(b => b.Beneficiary.Type == BeneficiaryTypes.Company);
        Assert.Equal(40_000m, company.Income);
        Assert.Equal(10_000m, company.NetGain);
        Assert.Equal(15_000m, company.ExtraTax);     // (40,000 + 20,000) x 25%
        Assert.Equal(0m, plan.TrusteeTax);
        Assert.True(plan.Beneficiaries.Single(b => b.Beneficiary.Name == "Pat").ExtraTax > 0);
    }

    [Fact]
    public void Income_no_one_is_entitled_to_is_taxed_to_the_trustee()
    {
        var plan = TrustPlanner.Work(2026, 100_000, 0, 0, true, [new PlanBeneficiary("Pat", BeneficiaryTypes.Individual, 80, 0)]);
        Assert.Equal(20_000m, plan.Unallocated);
        Assert.Equal(9_800m, plan.TrusteeTax);       // 47% plus 2% Medicare
    }
}
