using PortfolioManager.Core.Data;

namespace PortfolioManager.Tests;

public class AnsiTextTests
{
    [Fact]
    public void Sends_text_as_varchar()
    {
        var p = AnsiText.Of("BHP");
        Assert.True(p.IsAnsi);
        Assert.Equal("BHP", p.Value);
    }

    [Fact]
    public void Each_list_item_is_varchar()
    {
        var list = AnsiText.Of(["BHP", "CBA"]);
        Assert.Equal(["BHP", "CBA"], list.Select(p => p.Value));
        Assert.All(list, p => Assert.True(p.IsAnsi));
    }
}
