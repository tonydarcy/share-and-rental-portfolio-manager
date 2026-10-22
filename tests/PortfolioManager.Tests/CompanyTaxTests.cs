using PortfolioManager.Core.Payouts;
using PortfolioManager.Core.TaxWork;
using static PortfolioManager.Tests.TaxReconciliationTests;

namespace PortfolioManager.Tests;

public class CompanyTaxTests
{
    private static readonly DateOnly From = new(2025, 7, 1);
    private static readonly DateOnly To = new(2026, 6, 30);

    private static Payout Dividend(DateTime date, decimal credit) =>
        new(1, "ASX", "BHP", date, credit * 7 / 3, credit * 7 / 3, 0, credit, 0, 0, 0, 0, 0, 0, 0, "", true);

    [Theory]
    [InlineData(700, 100, 30, 300)]
    [InlineData(750, 100, 25, 250)]
    [InlineData(700, 50, 30, 150)]
    public void The_franking_debit_grosses_up_the_franked_part_of_a_dividend(decimal dividend, decimal franked, decimal rate, decimal debit) =>
        Assert.Equal(debit, FrankingLedger.FrankingDebit(dividend, franked, rate));

    [Fact]
    public void The_franking_account_runs_from_the_opening_balance_through_the_year()
    {
        FrankingEntry[] entries =
        [
            new(1, new(2024, 7, 1), FrankingKinds.Opening, "Opening balance", 1_000),
            new(2, new(2025, 10, 28), FrankingKinds.TaxPaid, "PAYG instalment", 500),
            new(3, new(2026, 3, 31), FrankingKinds.DividendPaid, "Dividend", -2_100),
        ];
        Payout[] payouts = [Dividend(new(2025, 3, 1), 200), Dividend(new(2025, 9, 1), 300)];

        var ledger = FrankingLedger.Build(entries, payouts, From, To);

        Assert.Equal(1_200, ledger.Opening);
        Assert.Equal(3, ledger.Lines.Count);
        Assert.Equal(800, ledger.Credits);
        Assert.Equal(2_100, ledger.Debits);
        Assert.Equal(-100, ledger.Closing);
        Assert.True(ledger.Deficit);
    }

    [Fact]
    public void Entries_after_the_year_are_left_out()
    {
        var ledger = FrankingLedger.Build([new(1, new(2026, 7, 1), FrankingKinds.TaxPaid, "Later", 50)], [], From, To);
        Assert.Empty(ledger.Lines);
        Assert.Equal(0, ledger.Closing);
    }

    [Fact]
    public void A_dividend_entry_is_stored_as_a_debit_with_its_working()
    {
        var input = new FrankingEntryInput { Kind = FrankingKinds.DividendPaid, Dividend = 7_000, FrankedPercent = 100, TaxRate = 30 };

        var (amount, description) = input.ToEntry();

        Assert.Equal(-3_000, amount);
        Assert.Contains("100% franked at 30%", description);
        Assert.Equal(-40, new FrankingEntryInput { Kind = FrankingKinds.Refund, Amount = 40 }.ToEntry().Amount);
    }

    [Fact]
    public void Deferred_tax_is_checked_against_the_market_value_adjustment()
    {
        var ledger = Year(new(1390, "Investments - Market Value Adjustment", "Asset", 10_000, 0),
            new(2850, "Deferred Income Tax Liability", "Liability", 0, 2_500),
            Expense(5950, "Income Tax Expense - Deferred", 2_500));

        var check = DeferredTaxCheck.From(ledger, 30);

        Assert.Equal(3_000, check.Expected);
        Assert.Equal(500, check.Difference);
        Assert.Equal(2_500, check.YearExpense);
        Assert.Equal(0, ledger.Expenses);
        Assert.Equal(2_500, ledger.IncomeTaxExpense);
    }

    [Theory]
    [InlineData(8.77, 19_715.97)]
    [InlineData(8.37, 19_450.97)]
    public void The_minimum_yearly_repayment_follows_the_ato_formula(decimal rate, decimal repayment) =>
        Assert.Equal(repayment, Div7a.MinimumRepayment(100_000, rate, 7));

    [Fact]
    public void A_div7a_schedule_pays_the_loan_off_over_the_term()
    {
        var schedule = Div7a.Schedule(100_000, 2025, 7, rateOverride: 8m);

        Assert.Equal(7, schedule.Count);
        Assert.Equal(8.77m, schedule[0].RatePercent);
        Assert.True(schedule[1].RateKnown);
        Assert.False(schedule[2].RateKnown);
        Assert.Equal(8m, schedule[2].RatePercent);
        Assert.InRange(schedule[^1].Closing, -0.05m, 0.05m);
    }
}
