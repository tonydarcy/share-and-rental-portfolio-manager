using PortfolioManager.Core.Accounting;
using PortfolioManager.Web.Api;

namespace PortfolioManager.Tests;

public class AgentAccountToolTests
{
    private static readonly AgentTools Tools = new();

    private static readonly IReadOnlyList<LedgerAccount> Chart =
    [
        new(1, 1100, "Cash at Bank", "Asset", "AUD", 0, "", null, true),
        new(2, 2300, "Property Loans", "Liability", "AUD", 7, "", null, true),
        new(3, 2301, "Loan - CBA", "Liability", "AUD", 7, "Property Loans", 2, true),
        new(4, 6010, "Accounting fees", "Expense", "AUD", 7, "", null, true),
        new(5, 1200, "Shares", "Asset", "AUD", 7, "", null, true),
    ];

    [Fact]
    public void The_account_tools_are_there_and_marked_as_writing_or_not()
    {
        Assert.False(Tools.Find("list_chart_of_accounts")!.Writes);
        Assert.True(Tools.Find("create_ledger_account")!.Writes);
        Assert.True(Tools.Find("edit_ledger_account")!.Writes);
    }

    [Fact]
    public void A_sub_account_takes_the_parents_type_and_the_next_number_after_it()
    {
        var input = AgentTools.NewAccount(Chart, "Loan - Westpac", null, null, 2);

        Assert.Equal("Liability", input.Type);
        Assert.Equal(2302, input.Number);
        Assert.Equal(2, input.ParentId);
        Assert.Empty(input.Validate(Chart));
    }

    [Fact]
    public void A_top_level_account_gets_the_next_free_ten_in_its_range()
    {
        var input = AgentTools.NewAccount(Chart, "Bank fees", "expense", null, null);

        Assert.Equal("Expense", input.Type);
        Assert.Equal(6020, input.Number);
        Assert.Null(input.ParentId);
    }

    [Fact]
    public void A_given_number_is_kept() =>
        Assert.Equal(2350, AgentTools.NewAccount(Chart, "Loan - NAB", null, 2350, 2).Number);

    [Fact]
    public void A_top_level_account_needs_a_type() =>
        Assert.Equal(400, Assert.Throws<AgentToolException>(() => AgentTools.NewAccount(Chart, "Something", null, null, null)).Status);

    [Fact]
    public void An_unknown_parent_is_refused() =>
        Assert.Equal(404, Assert.Throws<AgentToolException>(() => AgentTools.NewAccount(Chart, "Loan", null, null, 99)).Status);

    [Fact]
    public void An_unknown_type_is_refused() =>
        Assert.Equal(400, Assert.Throws<AgentToolException>(() => AgentTools.NewAccount(Chart, "Loan", "Loan", null, null)).Status);
}

public class AccountRollupTests
{
    private static readonly IReadOnlyList<LedgerAccount> Chart =
    [
        new(2, 2300, "Property Loans", "Liability", "AUD", 7, "", null, true),
        new(3, 2301, "Loan - CBA", "Liability", "AUD", 7, "Property Loans", 2, true),
        new(4, 2302, "Loan - ANZ", "Liability", "AUD", 7, "Property Loans", 2, true),
        new(5, 2100, "GST Payable", "Liability", "AUD", 7, "", null, true),
        new(6, 2400, "Credit card", "Liability", "AUD", 7, "", null, true),
    ];

    private static StatementLine Item(string label, decimal amount) => new(label, amount, "Item", 1, "Liability");

    [Fact]
    public void Sub_accounts_show_under_their_parent_with_its_total()
    {
        List<StatementLine> lines =
        [
            new("LIABILITIES", null, "Header", 0, "Liability"),
            Item("GST Payable", 100),
            Item("Loan - ANZ", 300000),
            Item("Loan - CBA", 200000),
            Item("Property Loans", 5000),
            new("Total Liabilities", 505100, "Total", 0, "Liability"),
        ];

        var result = AccountRollup.Apply(lines, Chart);

        Assert.Equal(
            ["LIABILITIES", "GST Payable", "Property Loans", "Property Loans (not in a sub-account)", "Loan - CBA", "Loan - ANZ", "Total Liabilities"],
            result.Select(l => l.Label));
        var group = result[2];
        Assert.True(group.IsGroup);
        Assert.Equal(505000, group.Amount);
        Assert.All(result.Skip(3).Take(3), l => Assert.Equal(2, l.Indent));
        // The items still add up to the total once
        Assert.Equal(505100, result.Where(l => l.RowType == "Item").Sum(l => l.Amount));
    }

    [Fact]
    public void A_parent_with_nothing_posted_still_heads_its_sub_accounts()
    {
        var result = AccountRollup.Apply([Item("Loan - CBA", 200000)], Chart);

        Assert.Equal(["Property Loans", "Loan - CBA"], result.Select(l => l.Label));
        Assert.Equal(200000, result[0].Amount);
    }

    [Fact]
    public void Accounts_with_no_sub_accounts_and_other_lines_are_left_alone()
    {
        List<StatementLine> lines = [Item("Credit card", 50), Item("GST Payable", 10), Item("Retained Earnings / Current Profit", 9)];

        Assert.Equal(lines, AccountRollup.Apply(lines, Chart));
    }

    [Fact]
    public void A_loop_in_the_parents_doesnt_hang()
    {
        IReadOnlyList<LedgerAccount> looped =
        [
            new(1, 2300, "A", "Liability", null, 7, "B", 2, true),
            new(2, 2301, "B", "Liability", null, 7, "A", 1, true),
        ];

        Assert.Equal(2, AccountRollup.Apply([Item("A", 1), Item("B", 2)], looped).Count(l => l.RowType == "Item"));
    }
}
