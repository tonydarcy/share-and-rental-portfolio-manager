using PortfolioManager.Web.Services;

namespace PortfolioManager.Tests;

public class WebManifestTests
{
    [Theory]
    [InlineData("Share Portfolio Manager", "SPM")]
    [InlineData("My Shares", "My Shares")]
    public void Short_name_fits_under_an_icon(string name, string shortName)
    {
        var m = (Dictionary<string, object>)WebManifest.For(name);
        Assert.Equal(name, m["name"]);
        Assert.Equal(shortName, m["short_name"]);
        Assert.Equal("standalone", m["display"]);
    }
}
