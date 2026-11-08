using Microsoft.AspNetCore.Http;
using PortfolioManager.Web.Pages.Account;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Tests;

/// <summary>The two-factor code page, and error replies that Safari would otherwise save as a file.</summary>
public class SignInPageTests
{
    private static string Repo([System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));

    [Theory]
    [InlineData(400)]
    [InlineData(404)]
    [InlineData(429)]
    public async Task Error_replies_on_pages_are_typed_html(int status)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/Account/LoginTwoFactor";
        context.Response.StatusCode = status;
        context.Response.Body = new MemoryStream();

        await PlainErrorPages.WriteAsync(context);

        Assert.Equal("text/html; charset=utf-8", context.Response.ContentType);
        context.Response.Body.Position = 0;
        var html = await new StreamReader(context.Response.Body).ReadToEndAsync();
        Assert.StartsWith("<!DOCTYPE html>", html);
        Assert.Contains(System.Net.WebUtility.HtmlEncode(PlainErrorPages.Message(status)), html);
    }

    [Theory]
    [InlineData("/api/v1/portfolios", true)]
    [InlineData("/mcp", true)]
    [InlineData("/oauth/token", true)]
    [InlineData("/.well-known/oauth-authorization-server", true)]
    [InlineData("/Account/Login", false)]
    [InlineData("/apiary", false)]
    public void Apps_keep_their_own_replies(string path, bool forApps) =>
        Assert.Equal(forApps, PlainErrorPages.ForApps(path));

    [Fact]
    public async Task Rate_limited_app_calls_get_plain_text()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/holdings";
        context.Response.StatusCode = 429;
        context.Response.Body = new MemoryStream();
        await PlainErrorPages.WriteAsync(context);
        Assert.Equal("text/plain; charset=utf-8", context.Response.ContentType);
    }

    [Fact]
    public void Login_and_code_pages_have_their_own_rate_limit()
    {
        HttpContext Post(string path)
        {
            var c = new DefaultHttpContext();
            c.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.5");
            c.Request.Path = path;
            return c;
        }
        Assert.NotEqual(SignInFlow.RateLimitKey(Post("/Account/Login")), SignInFlow.RateLimitKey(Post("/Account/LoginTwoFactor")));
        Assert.Equal(SignInFlow.RateLimitKey(Post("/Account/Login")), SignInFlow.RateLimitKey(Post("/account/login")));
        Assert.Equal(SignInFlow.RateLimitKey(Post("/Account/Login")), SignInFlow.RateLimitKey(Post("/Account/Login/")));
    }

    [Theory]
    [InlineData("123456", null, "123456")]
    [InlineData(" 123 456 ", "", "123 456")]
    [InlineData("123456", "abcde-fghjk", "abcde-fghjk")]
    [InlineData(null, "abcde-fghjk", "abcde-fghjk")]
    [InlineData("", "  ", null)]
    [InlineData(null, null, null)]
    public void Code_comes_from_the_recovery_box_when_used(string? totp, string? recovery, string? expected) =>
        Assert.Equal(expected, LoginTwoFactorModel.EnteredCode(totp, recovery));

    [Fact]
    public void Code_box_is_marked_for_password_managers()
    {
        var page = File.ReadAllText(Path.Combine(Repo(), "src", "PortfolioManager.Web", "Pages", "Account", "LoginTwoFactor.cshtml"));
        Assert.Contains("name=\"totp\"", page);
        Assert.Contains("autocomplete=\"one-time-code\"", page);
        Assert.Contains("inputmode=\"numeric\"", page);
        // The login's email on the page tells the password manager which saved login the code belongs to
        Assert.Contains("autocomplete=\"username\"", page);
    }
}
