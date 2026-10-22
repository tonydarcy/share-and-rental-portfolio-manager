using Microsoft.AspNetCore.Http;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Tests;

public class SiteBrandingTests
{
    private static HttpRequest Request(string host)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString(host);
        return context.Request;
    }

    [Fact]
    public void Links_use_the_configured_address_over_the_request_host()
    {
        var site = new SiteBranding { BaseUrl = "https://portfolios.example.com/" };
        Assert.Equal("https://portfolios.example.com/Account/ResetPassword?t=1", site.Link(Request("evil.example"), "/Account/ResetPassword?t=1"));
    }

    [Fact]
    public void Links_fall_back_to_the_request_host()
    {
        Assert.Equal("https://localhost:5001/Account/Login", new SiteBranding().Link(Request("localhost:5001"), "Account/Login"));
    }

    [Fact]
    public void Defaults_to_the_project_name_with_no_demo()
    {
        var site = new SiteBranding();
        Assert.Equal("Share Portfolio Manager", site.Name);
        Assert.False(site.HasDemo);
    }
}
