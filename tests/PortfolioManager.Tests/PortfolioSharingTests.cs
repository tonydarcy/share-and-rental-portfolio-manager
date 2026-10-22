using PortfolioManager.Core.Portfolios;

namespace PortfolioManager.Tests;

public class PortfolioSharingTests
{
    [Theory]
    [InlineData("View", PortfolioAccess.View)]
    [InlineData(" read only ", PortfolioAccess.View)]
    [InlineData("Read-only", PortfolioAccess.View)]
    [InlineData("Admin", PortfolioAccess.Full)]
    [InlineData("Edit", PortfolioAccess.Edit)]
    [InlineData(" editor ", PortfolioAccess.Edit)]
    [InlineData("", PortfolioAccess.Full)]
    [InlineData(null, PortfolioAccess.Full)]
    public void Only_look_only_words_mean_view_access(string? text, PortfolioAccess expected) =>
        Assert.Equal(expected, PortfolioService.ParseAccess(text));

    [Fact]
    public void View_access_cannot_edit()
    {
        Assert.False(new PortfolioSummary(1, "Trust", Access: PortfolioAccess.View).CanEdit);
        Assert.True(new PortfolioSummary(1, "Trust").CanEdit);
    }

    [Fact]
    public void Editors_can_change_but_only_owners_can_share()
    {
        var editor = new PortfolioSummary(1, "Trust", Access: PortfolioAccess.Edit);
        Assert.True(editor.CanEdit);
        Assert.False(editor.CanShare);
        Assert.True(new PortfolioSummary(1, "Trust").CanShare);
        Assert.False(new PortfolioSummary(1, "Trust", Access: PortfolioAccess.View).CanShare);
    }

    [Theory]
    [InlineData(PortfolioAccess.Full, "Admin")]
    [InlineData(PortfolioAccess.Edit, "Edit")]
    [InlineData(PortfolioAccess.View, "View")]
    public void Access_round_trips_through_the_database_text(PortfolioAccess access, string text)
    {
        Assert.Equal(text, PortfolioService.AccessText(access));
        Assert.Equal(access, PortfolioService.ParseAccess(text));
    }

    [Fact]
    public void Sharing_with_someone_not_signed_up_saves_an_invitation()
    {
        var invited = ShareResult.From("Invitation Saved");
        Assert.True(invited.Result.Ok);
        Assert.True(invited.Invited);

        var shared = ShareResult.From("Portfolio Shared");
        Assert.True(shared.Result.Ok);
        Assert.False(shared.Invited);

        var refused = ShareResult.From("Error: Only an owner of this portfolio can share it.");
        Assert.False(refused.Result.Ok);
        Assert.Equal("Only an owner of this portfolio can share it.", refused.Result.Error);
    }

    [Theory]
    [InlineData(null, "View")]
    [InlineData("  ", "View")]
    [InlineData("not-an-email", "View")]
    [InlineData("pat@example.com", "Owner")]
    public void Share_needs_an_email_and_a_known_access(string? email, string access) =>
        Assert.NotNull(PortfolioService.ValidateShare(email, access));

    [Theory]
    [InlineData("View")]
    [InlineData("Edit")]
    [InlineData("Full")]
    public void Share_accepts_view_edit_or_full(string access) =>
        Assert.Null(PortfolioService.ValidateShare(" pat@example.com ", access));
}
