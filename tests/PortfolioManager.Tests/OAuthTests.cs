using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using PortfolioManager.Web.Api;

namespace PortfolioManager.Tests;

public class OAuthTests
{
    private const string Claude = "https://claude.ai/api/mcp/auth_callback";
    private const string Verifier = "dBjftJeZ4CVP-mJ92K9pY1qX8wA3sQ7rT5uV6zH0kLmNoPqRsTuVwXyZ";

    private static OAuthServer Server() =>
        new(new EphemeralDataProtectionProvider(), new MemoryCache(Options.Create(new MemoryCacheOptions())));

    private static string RegisterClaude(OAuthServer server)
    {
        var (client, problem) = server.Register(new JsonObject { ["client_name"] = "Claude", ["redirect_uris"] = new JsonArray(Claude) });
        Assert.Null(problem);
        return client!["client_id"]!.GetValue<string>();
    }

    private static Dictionary<string, string?> Ask(string clientId) => new()
    {
        ["response_type"] = "code",
        ["client_id"] = clientId,
        ["redirect_uri"] = Claude,
        ["code_challenge"] = OAuthServer.S256(Verifier),
        ["code_challenge_method"] = "S256",
        ["state"] = "abc",
    };

    [Fact]
    public void Metadata_points_Claude_at_the_sign_in_addresses()
    {
        var resource = OAuthServer.ResourceMetadata("https://app.example.com", "Share Portfolio Manager");
        Assert.Equal("https://app.example.com/mcp", resource["resource"]!.GetValue<string>());
        Assert.Equal("https://app.example.com", resource["authorization_servers"]![0]!.GetValue<string>());

        var server = OAuthServer.ServerMetadata("https://app.example.com");
        Assert.Equal("https://app.example.com", server["issuer"]!.GetValue<string>());
        Assert.Equal("https://app.example.com/Account/Connect", server["authorization_endpoint"]!.GetValue<string>());
        Assert.Equal("https://app.example.com/oauth/token", server["token_endpoint"]!.GetValue<string>());
        Assert.Equal("https://app.example.com/oauth/register", server["registration_endpoint"]!.GetValue<string>());
        Assert.Equal("S256", server["code_challenge_methods_supported"]![0]!.GetValue<string>());

        Assert.Equal("Bearer resource_metadata=\"https://app.example.com/.well-known/oauth-protected-resource/mcp\"",
            OAuthServer.Challenge("https://app.example.com"));
    }

    [Fact]
    public void Registering_gives_a_client_id_that_carries_the_app()
    {
        var server = Server();
        var id = RegisterClaude(server);
        Assert.StartsWith("pmc_", id);
        var client = server.FindClient(id);
        Assert.Equal("Claude", client!.Name);
        Assert.Equal([Claude], client.RedirectUris);
        Assert.Null(server.FindClient(id[..^3] + "xyz"));
        Assert.Null(server.FindClient("pmc_not-base64!"));
        Assert.Null(Server().FindClient(id));
    }

    [Theory]
    [InlineData("https://claude.ai/api/mcp/auth_callback", true)]
    [InlineData("http://localhost:33418/callback", true)]
    [InlineData("http://127.0.0.1:5000/cb", true)]
    [InlineData("http://evil.example.com/cb", false)]
    [InlineData("https://claude.ai/cb#frag", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("/relative", false)]
    public void Only_safe_return_addresses_are_accepted(string uri, bool ok) => Assert.Equal(ok, OAuthServer.AllowedRedirect(uri));

    [Fact]
    public void Registering_without_a_good_return_address_is_refused()
    {
        var server = Server();
        Assert.Equal("invalid_redirect_uri", server.Register(new JsonObject { ["client_name"] = "x" }).Problem!.Error);
        Assert.Equal("invalid_redirect_uri",
            server.Register(new JsonObject { ["redirect_uris"] = new JsonArray("http://evil.example.com/cb") }).Problem!.Error);
        var (client, _) = server.Register(new JsonObject { ["redirect_uris"] = new JsonArray(Claude) });
        Assert.Equal("Connected app", client!["client_name"]!.GetValue<string>());
        Assert.Equal("none", client["token_endpoint_auth_method"]!.GetValue<string>());
    }

    [Fact]
    public void A_good_request_passes_and_bad_ones_say_why()
    {
        var server = Server();
        var id = RegisterClaude(server);
        var good = Ask(id);
        var (asked, problem) = server.Check(f => good.GetValueOrDefault(f));
        Assert.Null(problem);
        Assert.Equal(Claude, asked!.RedirectUri);
        Assert.Equal("abc", asked.State);

        // An unknown app or a return address it didn't register is shown on the page, never redirected to
        var other = new Dictionary<string, string?>(good) { ["redirect_uri"] = "https://evil.example.com/cb" };
        Assert.False(server.Check(f => other.GetValueOrDefault(f)).Problem!.Redirect);
        var forged = new Dictionary<string, string?>(good) { ["client_id"] = "pmc_forged" };
        Assert.False(server.Check(f => forged.GetValueOrDefault(f)).Problem!.Redirect);

        // Without PKCE the app is told so
        var noPkce = new Dictionary<string, string?>(good) { ["code_challenge_method"] = "plain" };
        var p = server.Check(f => noPkce.GetValueOrDefault(f)).Problem!;
        Assert.True(p.Redirect);
        Assert.Equal("invalid_request", p.Error);

        // With one registered return address it may be left out
        var noRedirect = new Dictionary<string, string?>(good) { ["redirect_uri"] = null };
        Assert.Equal(Claude, server.Check(f => noRedirect.GetValueOrDefault(f)).Request!.RedirectUri);
    }

    [Fact]
    public void A_code_swaps_once_with_the_right_verifier()
    {
        var server = Server();
        var id = RegisterClaude(server);
        var ask = Ask(id);
        var asked = server.Check(f => ask.GetValueOrDefault(f)).Request!;
        var code = server.IssueCode(asked, 42, readOnly: true);

        Assert.Null(server.Redeem(code, id, Claude, Verifier.Replace('d', 'e')));
        Assert.Null(server.Redeem(code, RegisterClaude(server), Claude, Verifier));
        Assert.Null(server.Redeem(code, id, "https://claude.ai/other", Verifier));

        var grant = server.Redeem(code, id, Claude, Verifier);
        Assert.Equal(new OAuthGrant(42, true, "Claude"), grant);
        Assert.Null(server.Redeem(code, id, Claude, Verifier));
        Assert.Null(server.Redeem(code[..^2] + "xx", id, Claude, Verifier));
    }

    [Fact]
    public void PKCE_challenge_is_base64url_sha256()
    {
        Assert.Equal("R-eNTUevE_lcjjNgv6cvqub5fUiKri2zssnIb1nqSZg", OAuthServer.S256("dBjftJeZ4CVP-mJ92K9pY1qX8wA3sQ7rT5uV6zH0kLm"));
    }

    [Fact]
    public void The_answer_is_added_to_the_return_address()
    {
        Assert.Equal("https://claude.ai/cb?code=x&state=s%20t", OAuthServer.RedirectWith("https://claude.ai/cb", ("code", "x"), ("state", "s t")));
        Assert.Equal("https://claude.ai/cb?a=1&code=x", OAuthServer.RedirectWith("https://claude.ai/cb?a=1", ("code", "x"), ("state", null)));
    }

    [Fact]
    public void Client_id_can_come_in_a_basic_header()
    {
        var header = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("pmc_abc:"));
        Assert.Equal("pmc_abc", OAuthEndpoints.BasicClientId(header));
        Assert.Null(OAuthEndpoints.BasicClientId("Bearer x"));
        Assert.Null(OAuthEndpoints.BasicClientId("Basic !!!"));
    }
}
