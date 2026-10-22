using PortfolioManager.Core.Accounting;

namespace PortfolioManager.Tests;

public class BankReconciliationTests
{
    private static BankRecLine Line(int id, decimal amount) => new(id, new DateOnly(2026, 6, 1), "x", "BankImport", amount, null);

    [Fact]
    public void Ticked_lines_go_to_the_proc_as_an_xml_list_without_repeats()
    {
        var input = new BankRecInput { Ticked = [5, 9, 5] };

        Assert.Equal("<i>5</i><i>9</i>", input.TickedXml);
        Assert.Equal("", new BankRecInput().TickedXml);
    }

    [Fact]
    public void The_difference_is_the_statement_less_the_opening_and_ticked_lines()
    {
        Assert.Equal(0m, BankRecInput.Difference(1000m, 1150m, [200m, -50m]));
        Assert.Equal(-25m, BankRecInput.Difference(1000m, 1125m, [200m, -50m]));
    }

    [Fact]
    public void A_finished_reconciliation_shows_a_difference_if_a_ticked_line_changed()
    {
        Assert.Equal(0m, new BankReconciliation(1, 2, new DateOnly(2026, 6, 30), 100m, 250m, DateTime.Today, 150m, 3).Difference);
        Assert.Equal(40m, new BankReconciliation(1, 2, new DateOnly(2026, 6, 30), 100m, 250m, DateTime.Today, 110m, 2).Difference);
    }

    [Fact]
    public void Deposits_and_withdrawals_split_by_sign()
    {
        Assert.Equal(20m, Line(1, 20m).Deposit);
        Assert.Equal(0m, Line(1, 20m).Withdrawal);
        Assert.Equal(15m, Line(2, -15m).Withdrawal);
    }

    [Fact]
    public void The_statement_plus_outstanding_deposits_less_unpresented_withdrawals_is_the_ledger_balance()
    {
        var summary = BankRecSummary.From(1000m, [Line(1, 300m), Line(2, -120m)], 1180m);

        Assert.Equal(300m, summary.OutstandingDeposits);
        Assert.Equal(120m, summary.UnpresentedWithdrawals);
        Assert.Equal(1180m, summary.Calculated);
        Assert.Equal(0m, summary.Unexplained);
    }

    [Fact]
    public void Anything_the_unticked_lines_dont_explain_is_shown()
    {
        Assert.Equal(-50m, BankRecSummary.From(1000m, [], 950m).Unexplained);
    }
}
