using PortfolioManager.Core.Portfolios;

namespace PortfolioManager.Tests;

public class DeletionTests
{
    [Theory]
    [InlineData("Smith Family Trust", "Smith Family Trust", true)]
    [InlineData("  smith family trust ", "Smith Family Trust", true)]
    [InlineData("Smith Family", "Smith Family Trust", false)]
    [InlineData("", "Smith Family Trust", false)]
    [InlineData(null, "Smith Family Trust", false)]
    [InlineData("", "", false)]
    [InlineData("tony@example.com", "Tony@Example.com", true)]
    public void The_typed_name_must_match_to_confirm(string? typed, string expected, bool confirms) =>
        Assert.Equal(confirms, DeletionService.Confirms(typed, expected));

    [Theory]
    [InlineData("Trades", DataPart.Trades)]
    [InlineData(" journals ", DataPart.Journals)]
    [InlineData("All", DataPart.All)]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("Accounts", null)]
    [InlineData("7", null)]
    public void Parts_parse_from_the_form(string? text, DataPart? expected) =>
        Assert.Equal(expected, DeletionService.ParsePart(text));

    [Fact]
    public void Every_part_has_words_for_the_page_and_a_name_the_proc_knows()
    {
        var procParts = new[] { "Trades", "Payouts", "Cash", "Journals", "Prices", "All" };
        foreach (var part in Enum.GetValues<DataPart>())
        {
            Assert.False(string.IsNullOrWhiteSpace(DeletionService.Title(part)));
            Assert.False(string.IsNullOrWhiteSpace(DeletionService.Describe(part)));
            Assert.Contains(part.ToString(), procParts);
        }
    }

    [Fact]
    public void Data_delete_maps_to_the_proc_parameters()
    {
        var p = DeletionService.DataParameters(5, 12, DataPart.Payouts);
        Assert.Equal(5, Prop<int>(p, "iUserId"));
        Assert.Equal(12, Prop<int>(p, "iPortfolioId"));
        Assert.Equal("Payouts", Prop<string>(p, "strPart"));
    }

    [Fact]
    public void Portfolio_delete_sends_the_trimmed_name()
    {
        var p = DeletionService.PortfolioParameters(5, 12, "  Trust ");
        Assert.Equal(12, Prop<int>(p, "iPortfolioId"));
        Assert.Equal("Trust", Prop<string>(p, "strConfirm"));
    }

    [Fact]
    public void Counts_add_up_for_everything()
    {
        var counts = new PortfolioDataCounts(Trades: 3, Payouts: 4, Cash: 5, Journals: 6, Prices: 7, LockedTo: null);
        Assert.Equal(3, counts.Of(DataPart.Trades));
        Assert.Equal(6, counts.Of(DataPart.Journals));
        Assert.Equal(25, counts.Of(DataPart.All));
    }

    [Fact]
    public void Shared_portfolios_are_kept_when_an_account_is_deleted()
    {
        Assert.True(new AccountPortfolioPlan(1, "Mine", OthersWithAccess: 0, OtherOwners: 0).WillBeDeleted);

        var withEditor = new AccountPortfolioPlan(2, "Shared", OthersWithAccess: 1, OtherOwners: 0);
        Assert.False(withEditor.WillBeDeleted);
        Assert.True(withEditor.OthersBecomeOwners);

        var withOwner = new AccountPortfolioPlan(3, "Co-owned", OthersWithAccess: 2, OtherOwners: 1);
        Assert.False(withOwner.WillBeDeleted);
        Assert.False(withOwner.OthersBecomeOwners);
    }

    private static T Prop<T>(object o, string name) => (T)o.GetType().GetProperty(name)!.GetValue(o)!;
}
