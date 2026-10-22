using PortfolioManager.Core.Clients;
using PortfolioManager.Core.Tax;
using PortfolioManager.Core.TaxWork;
using static PortfolioManager.Tests.TaxReconciliationTests;

namespace PortfolioManager.Tests;

public class TrustDistributionTests
{
    private static TaxWorkpaper Trust(decimal franked, decimal credits, decimal discountGain, decimal interest, decimal expenses = 0)
    {
        var client = ClientDetails.Unsaved(1, "Smith Family Trust", "Trust");
        var events = discountGain == 0 ? new List<CgtEvent>() : [new("ASX", "BHP", new(2020, 1, 1), new(2026, 3, 1), 100, 1000, 1000 + discountGain)];
        var report = new CgtReturnReport(CgtDiscount.Summarise(events, HalfRate), events, true, []);
        var ledger = Year(Revenue(4200, "Dividend Income", franked + credits), Revenue(4100, "Interest Income", interest),
            Revenue(4500, "Realised Gains on Investments", discountGain), Expense(6000, "Fees", expenses));
        return TaxWorkpaperService.Assemble(client, 2026, ledger, TaxYearData.Empty, report, Income(franked, credits, interest));
    }

    private static TrustBeneficiary B(int id, string name, decimal percent) => new(id, name, BeneficiaryTypes.Individual, percent);

    [Fact]
    public void Net_income_is_split_into_franked_capital_and_other_income()
    {
        var c = TrustDistributionStatement.Components(Trust(franked: 7_000, credits: 3_000, discountGain: 4_000, interest: 1_000));

        Assert.Equal(13_000, c.NetIncome);
        Assert.Equal(4_000, c.GrossCapitalGains);
        Assert.Equal(2_000, c.NetCapitalGain);
        Assert.Equal(1_000, c.Other);
    }

    [Fact]
    public void Each_beneficiary_gets_the_same_mix_and_the_columns_add_up()
    {
        var c = TrustDistributionStatement.Components(Trust(franked: 700, credits: 300, discountGain: 1_000, interest: 1_000.01m));

        var s = TrustDistributionStatement.Build(c, [B(1, "Anne", 33.3333m), B(2, "Bob", 33.3333m), B(3, "Cara", 33.3334m)]);

        Assert.Equal(c.NetIncome, s.Shares.Sum(x => x.Amounts.NetIncome));
        Assert.Equal(c.Other, s.Shares.Sum(x => x.Amounts.Other));
        Assert.Equal(0, s.Undistributed.NetIncome);
        Assert.Empty(s.Warnings);
    }

    [Fact]
    public void A_share_short_of_100_percent_is_left_to_the_trustee()
    {
        var c = TrustDistributionStatement.Components(Trust(0, 0, 0, interest: 1_000));

        var s = TrustDistributionStatement.Build(c, [B(1, "Anne", 60)]);

        Assert.Equal(400, s.Undistributed.NetIncome);
        Assert.Contains(s.Warnings, w => w.Contains("s99A"));
    }

    [Fact]
    public void A_loss_year_has_nothing_to_distribute()
    {
        var c = TrustDistributionStatement.Components(Trust(0, 0, 0, interest: 100, expenses: 500));

        var s = TrustDistributionStatement.Build(c, [B(1, "Anne", 100)]);

        Assert.Equal(0, s.Shares[0].Amounts.NetIncome);
        Assert.Contains(s.Warnings, w => w.Contains("no net income"));
    }

    [Fact]
    public void Expenses_that_eat_into_franked_income_are_flagged()
    {
        var c = TrustDistributionStatement.Components(Trust(franked: 700, credits: 300, discountGain: 0, interest: 0, expenses: 200));

        Assert.Equal(-200, c.Other);
        Assert.Contains(TrustDistributionStatement.Build(c, [B(1, "Anne", 100)]).Warnings, w => w.Contains("absorbed"));
    }

    [Fact]
    public void The_resolution_lists_each_beneficiary_and_is_made_by_30_June()
    {
        var client = ClientDetails.Unsaved(1, "Smith Family Trust", "Trust");

        var text = TrustDistributionStatement.ResolutionWording(client, 2026, [B(1, "Anne Smith", 50), B(2, "Bob Smith", 50)]);

        Assert.Contains(text, p => p.Contains("Anne Smith: 50%; Bob Smith: 50%"));
        Assert.Contains(text, p => p.Contains("on or before 30 June 2026"));
    }

    [Fact]
    public void A_corporate_trustee_signs_through_its_directors()
    {
        var client = ClientDetails.Unsaved(1, "Smith Family Trust", "Trust") with
        {
            TrusteeType = TrusteeTypes.Corporate,
            TrusteeName = "Smith Nominees Pty Ltd",
            Officeholders = [new("Anne Smith", OfficeholderRoles.TrusteeDirector), new("Accountant", OfficeholderRoles.Secretary)],
        };

        Assert.Equal(["Anne Smith"], TrustDistributionStatement.Signatories(client));
    }
}
