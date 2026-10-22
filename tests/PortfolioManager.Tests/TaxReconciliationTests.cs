using PortfolioManager.Core.Clients;
using PortfolioManager.Core.Payouts;
using PortfolioManager.Core.Statements;
using PortfolioManager.Core.Tax;
using PortfolioManager.Core.TaxWork;

namespace PortfolioManager.Tests;

public class TaxReconciliationTests
{
    internal static LedgerBalance Revenue(int number, string name, decimal amount) => new(number, name, "Revenue", 0, amount);
    internal static LedgerBalance Expense(int number, string name, decimal amount) => new(number, name, "Expense", amount, 0);

    /// <summary>A year whose opening ledger is empty, so each balance is the year's movement.</summary>
    internal static LedgerYear Year(params LedgerBalance[] closing) =>
        new(new LedgerSnapshot(new DateOnly(2025, 6, 30), [], 0, 0), new LedgerSnapshot(new DateOnly(2026, 6, 30), closing, 0, 0));

    internal static CgtSummary Gains(CgtDiscountRate rate, decimal discountGain = 0, decimal otherGain = 0, decimal loss = 0, decimal prior = 0)
    {
        var events = new List<CgtEvent>();
        if (discountGain != 0) events.Add(new("ASX", "BHP", new(2020, 1, 1), new(2026, 3, 1), 100, 1000, 1000 + discountGain));
        if (otherGain != 0) events.Add(new("ASX", "CBA", new(2026, 1, 1), new(2026, 3, 1), 100, 1000, 1000 + otherGain));
        if (loss != 0) events.Add(new("ASX", "WES", new(2026, 1, 1), new(2026, 3, 1), 100, 1000, 1000 - loss));
        return CgtDiscount.Summarise(events, rate, prior);
    }

    internal static readonly CgtDiscountRate CompanyRate = new(CgtEntityKind.Company, 0m, false);
    internal static readonly CgtDiscountRate HalfRate = new(CgtEntityKind.Trust, 0.5m, false);
    internal static readonly CgtDiscountRate ThirdRate = new(CgtEntityKind.SuperFund, 1m / 3m, false);

    internal static TaxIncomeFacts Income(decimal franked = 0, decimal credits = 0, decimal interest = 0, decimal tfn = 0, decimal foreign = 0) =>
        new(interest, 0, franked, credits, 0, foreign, 0, tfn, 0);

    internal static TaxYearData Data(decimal losses = 0, decimal? rate = null, decimal? ecpi = null, decimal payg = 0, params TaxAdjustment[] adjustments) =>
        new(new TaxYearSettings(losses, null, rate, ecpi, payg, null), adjustments);

    [Fact]
    public void A_company_swaps_its_accounting_gain_for_the_net_capital_gain_and_adds_back_non_deductible_items()
    {
        var ledger = Year(Revenue(4200, "Dividend Income", 10_000), Revenue(4500, "Realised Gains on Investments", 5_000),
            Revenue(4600, "Unrealised Gains on Investments", 2_000), Expense(6000, "Accounting Fees", 1_000));
        var data = Data(adjustments: new TaxAdjustment(1, TaxAdjustmentKinds.AddBack, "Entertainment", 200));

        var rec = TaxReconciliation.Build(ClientEntityTypes.Company, ledger, Gains(CompanyRate, otherGain: 5_000), Income(7_000, 3_000), data);

        Assert.Equal(16_000, rec.ProfitBeforeTax);
        Assert.Equal(14_200, rec.TaxableIncome);
        Assert.Contains(rec.Lines, l => l.Style == TaxRecStyle.Info && l.Amount == 3_000);
    }

    [Fact]
    public void Company_tax_takes_franking_credits_off_and_leaves_the_rest_to_pay()
    {
        var ledger = Year(Revenue(4200, "Dividend Income", 10_000), Revenue(4500, "Realised Gains on Investments", 5_000), Expense(6000, "Fees", 1_000));
        var data = Data(payg: 500, adjustments: new TaxAdjustment(1, TaxAdjustmentKinds.AddBack, "Entertainment", 200));
        var rec = TaxReconciliation.Build(ClientEntityTypes.Company, ledger, Gains(CompanyRate, otherGain: 5_000), Income(7_000, 3_000), data);

        var tax = TaxCalculation.For(ClientEntityTypes.Company, rec, Income(7_000, 3_000), data.Settings)!;

        Assert.Equal(4_260, tax.GrossTax);
        Assert.Equal(3_000, tax.FrankingOffset);
        Assert.Equal(1_260, tax.TaxAfterOffsets);
        Assert.Equal(760, tax.Payable);
    }

    [Fact]
    public void A_companys_excess_franking_credits_are_not_refunded_but_convert_to_a_loss()
    {
        var ledger = Year(Revenue(4200, "Dividend Income", 1_000), Expense(6000, "Interest", 900));
        var data = Data(rate: 25m);
        var rec = TaxReconciliation.Build(ClientEntityTypes.Company, ledger, Gains(CompanyRate), Income(700, 300), data);

        var tax = TaxCalculation.For(ClientEntityTypes.Company, rec, Income(700, 300), data.Settings)!;

        Assert.Equal(25, tax.GrossTax);
        Assert.Equal(25, tax.FrankingOffset);
        Assert.Equal(275, tax.ExcessFranking);
        Assert.Equal(1_100, tax.ExcessFrankingLoss);
        Assert.Equal(0, tax.Payable);
    }

    [Fact]
    public void A_trust_includes_the_discounted_gain_in_net_income()
    {
        var ledger = Year(Revenue(4100, "Interest Income", 1_000), Revenue(4500, "Realised Gains on Investments", 10_000));

        var rec = TaxReconciliation.Build(ClientEntityTypes.Trust, ledger, Gains(HalfRate, discountGain: 10_000), TaxIncomeFacts.None, TaxYearData.Empty);

        Assert.Equal(6_000, rec.TaxableIncome);
        Assert.Equal("Net income of the trust (s95)", rec.ResultLabel);
        Assert.Null(TaxCalculation.For(ClientEntityTypes.Trust, rec, TaxIncomeFacts.None, TaxYearData.Empty.Settings));
    }

    [Fact]
    public void Tax_losses_are_applied_up_to_the_income_and_the_rest_carried_forward()
    {
        var ledger = Year(Revenue(4100, "Interest Income", 1_000));

        var rec = TaxReconciliation.Build(ClientEntityTypes.Company, ledger, Gains(CompanyRate), TaxIncomeFacts.None, Data(losses: 1_500));

        Assert.Equal(1_000, rec.LossesApplied);
        Assert.Equal(0, rec.TaxableIncome);
        Assert.Equal(500, rec.LossesCarriedForward);
    }

    [Fact]
    public void A_loss_year_adds_to_the_losses_carried_forward()
    {
        var ledger = Year(Revenue(4100, "Interest Income", 1_000), Expense(6000, "Interest Expense", 1_600));

        var rec = TaxReconciliation.Build(ClientEntityTypes.Trust, ledger, Gains(HalfRate), TaxIncomeFacts.None, Data(losses: 100));

        Assert.Equal(0, rec.TaxableIncome);
        Assert.Equal(700, rec.LossesCarriedForward);
    }

    [Fact]
    public void A_partnerships_capital_gains_are_left_to_the_partners()
    {
        var ledger = Year(Revenue(4100, "Interest Income", 1_000), Revenue(4500, "Realised Gains on Investments", 4_000));

        var rec = TaxReconciliation.Build(ClientEntityTypes.Partnership, ledger, Gains(HalfRate, otherGain: 4_000), TaxIncomeFacts.None, TaxYearData.Empty);

        Assert.Equal(1_000, rec.TaxableIncome);
        Assert.Contains(rec.Lines, l => l.Style == TaxRecStyle.Info && l.Amount == 4_000);
    }

    [Fact]
    public void An_smsf_takes_off_non_concessional_contributions_and_exempt_pension_income()
    {
        var ledger = Year(Revenue(4100, "Interest Income", 10_000), Revenue(4800, "Member Contributions", 30_000), Expense(6000, "Audit Fee", 2_000));
        var smsf = new SmsfTaxInputs(Concessional: 20_000, NonConcessional: 10_000, Rollovers: 0);

        var rec = TaxReconciliation.Build(ClientEntityTypes.Smsf, ledger, Gains(ThirdRate), TaxIncomeFacts.None, Data(ecpi: 50m), smsf);

        Assert.Equal(5_000, rec.ExemptPensionIncome);
        Assert.Equal(24_000, rec.TaxableIncome);
    }

    [Fact]
    public void An_smsfs_franking_credits_are_refundable_and_the_levy_is_added()
    {
        var ledger = Year(Revenue(4200, "Dividend Income", 1_000));
        var rec = TaxReconciliation.Build(ClientEntityTypes.Smsf, ledger, Gains(ThirdRate), Income(700, 300), TaxYearData.Empty, SmsfTaxInputs.None);

        var tax = TaxCalculation.For(ClientEntityTypes.Smsf, rec, Income(700, 300), TaxYearData.Empty.Settings)!;

        Assert.Equal(150, tax.GrossTax);
        Assert.Equal(300, tax.FrankingOffset);
        Assert.Equal(-150 + TaxCalculation.SmsfLevy, tax.Payable);
    }

    [Fact]
    public void Entered_capital_losses_replace_the_worked_out_ones()
    {
        var client = ClientDetails.Unsaved(1, "Smith Family Trust", "Trust");
        var events = new List<CgtEvent> { new("ASX", "BHP", new(2020, 1, 1), new(2026, 3, 1), 100, 1000, 5000) };
        var report = new CgtReturnReport(CgtDiscount.Summarise(events, HalfRate), events, true, []);
        var data = new TaxYearData(TaxYearSettings.Default with { CapitalLossesBroughtForward = 1_000 }, []);

        var w = TaxWorkpaperService.Assemble(client, 2026, Year(), data, report, TaxIncomeFacts.None);

        Assert.Equal(1_500, w.Cgt.NetCapitalGain);
        Assert.Contains(w.Warnings, x => x.Contains("client details"));
    }

    [Fact]
    public void Tfn_withheld_is_split_between_interest_and_dividends()
    {
        Payout P(decimal interest, decimal franked, decimal tfn) =>
            new(1, "ASX", "X", null, interest + franked, franked, 0, 0, interest, 0, tfn, 0, 0, 0, 0, "", true);

        var facts = TaxIncomeFacts.From([P(100, 0, 47), P(0, 200, 94)]);

        Assert.Equal(47, facts.TfnOnInterest);
        Assert.Equal(94, facts.TfnOnDividends);
        Assert.Equal(141, facts.TfnWithheld);
    }

    [Fact]
    public void Tax_year_rows_map_with_defaults_for_a_year_never_saved()
    {
        var data = TaxYearService.Map(null, [new Dictionary<string, object?> { ["iAdjustmentID"] = 3, ["strKind"] = "NonAssessable", ["strDescription"] = "Refund", ["dblAmount"] = 50m }]);

        Assert.Equal(TaxYearSettings.Default, data.Settings);
        Assert.Equal(-50, Assert.Single(data.Adjustments).Signed);
    }
}
