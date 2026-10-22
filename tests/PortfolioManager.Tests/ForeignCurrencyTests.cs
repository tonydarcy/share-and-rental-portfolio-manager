using PortfolioManager.Core.Accounting;

namespace PortfolioManager.Tests;

public class ForeignCurrencyTests
{
    private const int Ar = 1, Ap = 2, Fx = 3;
    private static readonly Dictionary<string, decimal> Rates = new() { ["USD"] = 0.625m };

    [Fact]
    public void Invoices_and_bills_revalue_against_the_gains_account_and_reverse()
    {
        // USD 1,000 invoice carried at 1,500, now 1,600 (gain 100); USD 500 bill carried at 750, now 800 (loss 50)
        var lines = FxRevaluationRules.Lines([
            new FxOpenItem(FxRevaluationRules.Invoice, 1, "INV-1", "USD", 1_000, 1_500),
            new FxOpenItem(FxRevaluationRules.Bill, 2, "B-1", "USD", 500, 750),
        ], Rates, Ar, Ap, Fx);
        Assert.Equal([(Ar, 100m, 0m, true), (Ap, 0m, 50m, true), (Fx, 0m, 50m, true)], lines.Select(l => (l.AccountId, l.Debit, l.Credit, l.Reverse)));
        Assert.Equal(lines.Sum(l => l.Debit), lines.Sum(l => l.Credit));
    }

    [Fact]
    public void Account_revaluations_stay_and_missing_rates_are_skipped()
    {
        var lines = FxRevaluationRules.Lines([
            new FxOpenItem(FxRevaluationRules.Account, 77, "1015 - USD bank", "USD", 200, 330),
            new FxOpenItem(FxRevaluationRules.Invoice, 1, "INV-1", "GBP", 1_000, 1_900),
        ], Rates, Ar, Ap, Fx);
        Assert.Equal([(77, 0m, 10m, false), (Fx, 10m, 0m, false)], lines.Select(l => (l.AccountId, l.Debit, l.Credit, l.Reverse)));
    }

    [Fact]
    public void Open_item_revalues_at_the_rate()
    {
        var item = new FxOpenItem(FxRevaluationRules.Invoice, 1, "INV-1", "USD", 1_000, 1_500);
        Assert.Equal(1_600m, item.Revalued(0.625m));
        Assert.Equal(100m, item.Change(0.625m));
    }

    [Fact]
    public void Rates_text_is_sorted_and_invariant() =>
        Assert.Equal("GBP 0.48, USD 0.655", FxRevaluationRules.RatesText(new Dictionary<string, decimal> { ["USD"] = 0.655m, ["GBP"] = 0.48m }));
}
