using PortfolioManager.Core.Accounting;

namespace PortfolioManager.Tests;

public class AllocationRuleTests
{
    private static UnallocatedLine Line(string description, decimal amount, int id = 1) => new(id, new DateOnly(2026, 3, 1), description, amount);

    private static AllocationRule Rule(int id, string contains, int account, string direction = RuleDirection.Any, int order = 0) =>
        new(id, contains, direction, account, $"Account {account}", null, order);

    [Fact]
    public void A_rule_matches_text_anywhere_ignoring_case_and_spacing()
    {
        var rule = Rule(1, "account  fee", 6000);

        Assert.True(rule.Matches(Line("MONTHLY ACCOUNT FEE 03/26", -5m)));
        Assert.False(rule.Matches(Line("ACCOUNT KEEPING", -5m)));
    }

    [Fact]
    public void A_rule_can_be_only_for_deposits_or_withdrawals()
    {
        var deposits = Rule(1, "ATO", 1400, RuleDirection.Deposit);
        var withdrawals = Rule(2, "ATO", 2100, RuleDirection.Withdrawal);

        Assert.True(deposits.Matches(Line("ATO REFUND", 900m)));
        Assert.False(deposits.Matches(Line("ATO PAYMENT", -900m)));
        Assert.True(withdrawals.Matches(Line("ATO PAYMENT", -900m)));
    }

    [Fact]
    public void The_first_rule_in_order_wins()
    {
        AllocationRule[] rules = [Rule(1, "interest", 4100, order: 20), Rule(2, "interest charged", 6100, order: 10)];

        Assert.Equal(6100, AllocationRule.FirstMatch(rules, Line("INTEREST CHARGED", -3m))!.AccountId);
        Assert.Equal(4100, AllocationRule.FirstMatch(rules, Line("INTEREST PAID", 3m))!.AccountId);
        Assert.Null(AllocationRule.FirstMatch(rules, Line("TRANSFER", 3m)));
    }

    [Fact]
    public void An_empty_rule_matches_nothing()
    {
        Assert.False(Rule(1, "  ", 6000).Matches(Line("anything", 1m)));
    }

    [Theory]
    [InlineData("ACCOUNT FEE 03/26 REF 12345", "ACCOUNT FEE")]
    [InlineData("Direct Credit 401234 BHP GROUP DIV", "Direct Credit")]
    [InlineData("123456 7890 TRANSFER", "123456 7890 TRANSFER")]
    [InlineData("  Netbank   transfer  ", "Netbank transfer")]
    public void A_suggested_rule_keeps_the_words_before_the_numbers(string description, string expected)
    {
        Assert.Equal(expected, AllocationRule.Suggest(description));
    }

    [Fact]
    public void Rules_map_with_the_account_number_and_name()
    {
        var rows = new[]
        {
            new Dictionary<string, object?>
            {
                ["iRuleID"] = 7, ["strContains"] = "FEE", ["strDirection"] = "Withdrawal", ["iAccountID"] = 55,
                ["strDescription"] = null, ["iSortOrder"] = 10, ["iAccountNumber"] = 6000, ["strAccountName"] = "Bank Fees",
            },
        };

        var rule = Assert.Single(AllocationRuleService.Map(rows));
        Assert.Equal("6000 - Bank Fees", rule.AccountName);
        Assert.Equal(RuleDirection.Withdrawal, rule.Direction);
        Assert.Null(rule.Description);
    }
}
