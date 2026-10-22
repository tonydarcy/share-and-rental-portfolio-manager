using PortfolioManager.Core.Clients;
using PortfolioManager.Core.Tax;
using PortfolioManager.Core.TaxWork;
using static PortfolioManager.Tests.TaxReconciliationTests;

namespace PortfolioManager.Tests;

public class SmsfMemberTests
{
    private static readonly DateOnly July1 = new(2025, 7, 1);

    private static SmsfMemberAccount Acc(int id, int member, string phase, decimal opening, decimal cc = 0, decimal ncc = 0, decimal pension = 0) =>
        new(id, member, 2026, phase, opening, cc, ncc, 0, pension, 0, 0);

    [Theory]
    [InlineData(64, 4)]
    [InlineData(65, 5)]
    [InlineData(74, 5)]
    [InlineData(75, 6)]
    [InlineData(84, 7)]
    [InlineData(85, 9)]
    [InlineData(92, 11)]
    [InlineData(95, 14)]
    public void Minimum_pension_factors_follow_age(int age, decimal factor) => Assert.Equal(factor, MinimumPension.Factor(age));

    [Fact]
    public void The_minimum_pension_is_rounded_to_the_nearest_ten_dollars() =>
        Assert.Equal(25_620, MinimumPension.For(512_345, 70));

    [Fact]
    public void Age_is_counted_at_the_start_of_the_year()
    {
        var m = new SmsfMember(1, "Anne", new DateOnly(1960, 7, 2));
        Assert.Equal(64, m.AgeAt(July1));
        Assert.Equal(65, (m with { BirthDate = new DateOnly(1960, 7, 1) }).AgeAt(July1));
    }

    [Theory]
    [InlineData(1_000_000, 360_000)]
    [InlineData(1_760_000, 240_000)]
    [InlineData(1_880_000, 120_000)]
    [InlineData(2_000_000, 0)]
    public void The_non_concessional_limit_depends_on_the_total_super_balance(decimal tsb, decimal limit) =>
        Assert.Equal(limit, ContributionCaps.For(2026).NonConcessionalLimit(tsb, 60));

    [Fact]
    public void Members_75_and_over_have_no_non_concessional_limit() =>
        Assert.Equal(0, ContributionCaps.For(2026).NonConcessionalLimit(100_000, 75));

    [Fact]
    public void A_year_without_loaded_caps_uses_the_latest_and_says_so()
    {
        var caps = ContributionCaps.For(2030);
        Assert.False(caps.Known);
        Assert.Equal(ContributionCaps.For(2026).Concessional, caps.Concessional);
    }

    [Fact]
    public void Earnings_follow_weighted_balances_and_tax_stays_off_pension_accounts()
    {
        var data = new SmsfYearData(
            [new(1, "Anne", new DateOnly(1955, 1, 1)), new(2, "Bob", new DateOnly(1980, 1, 1))],
            [Acc(10, 1, SmsfPhases.Retirement, 600_000, pension: 40_000), Acc(20, 2, SmsfPhases.Accumulation, 200_000, cc: 20_000)]);

        var result = SmsfAllocation.Build(2026, data, earnings: 80_000, fundTax: 5_000, July1);

        var anne = result.Single(m => m.Member.Name == "Anne").Accounts.Single();
        var bob = result.Single(m => m.Member.Name == "Bob").Accounts.Single();
        // Weighted: 580,000 and 210,000
        Assert.Equal(80_000, anne.Earnings + bob.Earnings);
        Assert.Equal(58_734.18m, anne.Earnings);
        Assert.Equal(0, anne.IncomeTax);
        Assert.Equal(3_000, bob.ContributionsTax);
        Assert.Equal(2_000, bob.IncomeTax);
        Assert.Equal(600_000 + 58_734.18m - 40_000, anne.Closing);
    }

    [Fact]
    public void Caps_and_pension_checks_flag_problems()
    {
        var data = new SmsfYearData(
            [new(1, "Anne", new DateOnly(1955, 1, 1))],
            [Acc(10, 1, SmsfPhases.Retirement, 500_000, pension: 10_000), Acc(11, 1, SmsfPhases.Accumulation, 1_300_000, cc: 35_000, ncc: 250_000)]);

        var anne = Assert.Single(SmsfAllocation.Build(2026, data, 0, 0, July1));

        Assert.Equal(70, anne.Age);
        Assert.True(anne.OverConcessionalCap);
        Assert.Equal(240_000, anne.NonConcessionalLimit);
        Assert.True(anne.OverNonConcessionalCap);
        Assert.Equal(25_000, anne.MinimumPension);
        Assert.True(anne.PensionShort);
    }

    [Fact]
    public void Contributions_credited_to_equity_are_added_to_taxable_income()
    {
        var ledger = Year(Revenue(4100, "Interest Income", 10_000));
        var rec = TaxReconciliation.Build(ClientEntityTypes.Smsf, ledger, Gains(ThirdRate), TaxIncomeFacts.None, TaxYearData.Empty, new SmsfTaxInputs(20_000, 50_000, 0));

        Assert.Equal(30_000, rec.TaxableIncome);
    }

    [Fact]
    public void The_workpaper_allocates_the_funds_earnings_and_tax_to_members()
    {
        var client = ClientDetails.Unsaved(1, "Smith Super Fund", "SMSF");
        var report = new CgtReturnReport(CgtDiscount.Summarise([], ThirdRate), [], true, []);
        var ledger = Year(Revenue(4100, "Interest Income", 10_000), Revenue(4800, "Member Contributions", 20_000));
        var smsf = new SmsfYearData([new(1, "Anne", null)], [Acc(10, 1, SmsfPhases.Accumulation, 100_000, cc: 20_000)]);

        var w = TaxWorkpaperService.Assemble(client, 2026, ledger, TaxYearData.Empty, report, TaxIncomeFacts.None, smsf);

        var anne = Assert.Single(w.Members!).Accounts.Single();
        Assert.Equal(10_000, anne.Earnings);
        Assert.Equal(4_500, anne.ContributionsTax + anne.IncomeTax);
        Assert.Equal(w.Tax!.TaxAfterOffsets, anne.ContributionsTax + anne.IncomeTax);
    }

    [Fact]
    public void Member_rows_map_from_the_proc()
    {
        var data = SmsfMemberService.Map(
            [new Dictionary<string, object?> { ["iMemberID"] = 1, ["strName"] = "Anne", ["dtBirthDate"] = new DateTime(1955, 1, 1) }],
            [new Dictionary<string, object?> { ["iMemberAccountID"] = 5, ["iMemberID"] = 1, ["iYear"] = 2026, ["strPhase"] = "Retirement", ["dblOpeningBalance"] = 1000m }]);

        Assert.Equal(new DateOnly(1955, 1, 1), data.Members[0].BirthDate);
        Assert.True(data.Accounts[0].IsPension);
        Assert.Equal(0, data.TaxInputs.Concessional);
    }
}
