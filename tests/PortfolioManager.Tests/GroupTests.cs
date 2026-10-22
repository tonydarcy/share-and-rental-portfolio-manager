using PortfolioManager.Core.Groups;

namespace PortfolioManager.Tests;

public class GroupTests
{
    private static readonly HashSet<int> PortfolioTrades = [1, 2, 3, 4, 5];

    [Fact]
    public void Changes_add_newly_ticked_and_remove_unticked_trades()
    {
        var c = GroupService.Changes([1, 2, 5], inGroup: new HashSet<int> { 2, 3 }, PortfolioTrades);
        Assert.Equal([1, 5], c.Add);
        Assert.Equal([3], c.Remove);
    }

    [Fact]
    public void Changes_ignore_trades_from_other_portfolios() =>
        Assert.Empty(GroupService.Changes([99, 100], new HashSet<int>(), PortfolioTrades).Add);

    [Fact]
    public void Nothing_ticked_empties_the_group()
    {
        var c = GroupService.Changes([], new HashSet<int> { 4 }, PortfolioTrades);
        Assert.Empty(c.Add);
        Assert.Equal([4], c.Remove);
    }

    [Fact]
    public void Same_ticks_mean_no_changes() =>
        Assert.True(GroupService.Changes([2, 2, 3], new HashSet<int> { 2, 3 }, PortfolioTrades).IsEmpty);

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public void Group_needs_a_name(string? name) => Assert.NotNull(GroupService.ValidateName(name));

    [Fact]
    public void Group_name_is_limited_to_100_characters()
    {
        Assert.Null(GroupService.ValidateName(new string('a', 100)));
        Assert.NotNull(GroupService.ValidateName(new string('a', 101)));
    }
}
