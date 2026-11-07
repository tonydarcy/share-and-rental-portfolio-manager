using PortfolioManager.Core.Debt;
using PortfolioManager.Core.TaxWork;

namespace PortfolioManager.Tests;

public class LoanLedgerAccountTests
{
    private static readonly Loan Home = new(7, "ANZ property loan", "ANZ", LoanKinds.Investment, 6m, 100m, null, new(2024, 7, 1), null,
        AccountId: 900, InterestAccountId: 901);

    private static LedgerLoanMove Move(int journal, DateOnly date, decimal loanCredit, decimal interest) =>
        new(Home.Id, journal, date, "x", loanCredit, interest);

    [Fact]
    public void Opening_balance_journal_is_a_drawdown()
    {
        var e = Assert.Single(LedgerLoanEntries.From([Move(1, new(2024, 7, 1), 500_000m, 0)]));
        Assert.Equal(LoanEntryKinds.Drawdown, e.Kind);
        Assert.Equal(500_000m, e.Amount);
        Assert.True(e.FromLedger);
    }

    [Fact]
    public void Interest_charged_to_the_loan_adds_to_the_balance()
    {
        var e = Assert.Single(LedgerLoanEntries.From([Move(2, new(2024, 7, 31), 2_500m, 2_500m)]));
        Assert.Equal(LoanEntryKinds.Interest, e.Kind);
        Assert.Equal(2_500m, e.Signed);
    }

    [Fact]
    public void Interest_paid_from_the_bank_leaves_the_balance_alone()
    {
        var e = Assert.Single(LedgerLoanEntries.From([Move(3, new(2024, 8, 31), 0, 2_400m)]));
        Assert.Equal(LoanEntryKinds.InterestPaid, e.Kind);
        Assert.Equal(0, e.Signed);
    }

    [Fact]
    public void Repayment_with_interest_from_the_bank_splits()
    {
        var list = LedgerLoanEntries.From([Move(4, new(2024, 9, 30), -1_000m, 2_400m)]);
        Assert.Equal(-1_000m, list.Single(e => e.Kind == LoanEntryKinds.Repayment).Signed);
        Assert.Equal(2_400m, list.Single(e => e.Kind == LoanEntryKinds.InterestPaid).Amount);
    }

    [Fact]
    public void Year_reads_balance_and_interest_from_the_ledger()
    {
        var entries = LedgerLoanEntries.From([
            Move(1, new(2024, 7, 1), 500_000m, 0),
            Move(2, new(2024, 7, 31), 2_500m, 2_500m),
            Move(3, new(2024, 8, 31), -3_000m, 0),
            Move(4, new(2024, 9, 30), 0, 2_400m),
        ]);
        var y = LoanCalc.Year(Home, entries, new(2024, 7, 1), new(2025, 6, 30));
        Assert.Equal(499_500m, y.ClosingBalance);
        Assert.Equal(4_900m, y.Interest);
        Assert.Equal(4_900m, y.Deductible);
    }

    [Fact]
    public void Tax_reconciliation_leaves_ledger_interest_to_the_ledger()
    {
        var entries = LedgerLoanEntries.From([Move(4, new(2024, 9, 30), 0, 2_400m)]);
        var year = LoanInterestYear.For([Home], entries, new Dictionary<int, int>(), 2025);
        Assert.Equal(0, year.Charged);
    }

    [Fact]
    public void Interest_paid_is_not_offered_as_an_entry_type() => Assert.DoesNotContain(LoanEntryKinds.InterestPaid, LoanEntryKinds.All);
}
