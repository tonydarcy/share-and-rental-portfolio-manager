using PortfolioManager.Core.Alerts;
using PortfolioManager.Core.Dashboard;
using PortfolioManager.Core.Prices;
using PortfolioManager.Web.Api;

namespace PortfolioManager.Tests;

public class MobileApiTests
{
    [Theory]
    [InlineData("CBA", "CBA")]
    [InlineData("50%", "50\\%")]
    [InlineData("A_B", "A\\_B")]
    [InlineData("[x]", "\\[x]")]
    [InlineData("a\\b", "a\\\\b")]
    public void Search_text_matches_only_itself(string text, string escaped) => Assert.Equal(escaped, QuoteService.LikeEscape(text));

    [Theory]
    [InlineData("android", "abc:123", true)]
    [InlineData("IOS", "abc", true)]
    [InlineData("windows", "abc", false)]
    [InlineData("android", "", false)]
    [InlineData("android", "has space", false)]
    [InlineData(null, "abc", false)]
    public void Phone_registrations_are_checked(string? platform, string token, bool ok) =>
        Assert.Equal(ok, PushRules.Validate(platform, token) is null);

    [Fact]
    public void Overlong_tokens_and_names_are_refused_or_cut()
    {
        Assert.NotNull(PushRules.Validate("android", new string('x', PushRules.MaxTokenLength + 1)));
        Assert.Equal(PushRules.MaxDeviceName, PushRules.CleanName(new string('n', 300))!.Length);
        Assert.Null(PushRules.CleanName("  "));
    }

    [Fact]
    public void One_alert_is_its_own_notification()
    {
        var m = PushRules.ForDigest(new AlertDigest(1, 42, "a@b.c", "Family", [new AlertItem(AlertKinds.Price, "CBA reached $150")]));
        Assert.Equal("Family", m.Title);
        Assert.Equal("CBA reached $150", m.Body);
        Assert.Equal("42", m.Data["portfolioId"]);
        Assert.Equal(AlertKinds.Price, m.Data["kind"]);
    }

    [Fact]
    public void Many_alerts_show_five_and_a_count()
    {
        var items = Enumerable.Range(1, 7).Select(i => new AlertItem(i % 2 == 0 ? AlertKinds.Price : AlertKinds.Dividend, $"Alert {i}")).ToList();
        var m = PushRules.ForDigest(new AlertDigest(1, 2, "a@b.c", "Super", items));
        Assert.Equal("Super: 7 alerts", m.Title);
        Assert.Equal(6, m.Body.Split('\n').Length);
        Assert.EndsWith("and 2 more", m.Body);
        Assert.Equal("Alerts", m.Data["kind"]);
    }

    [Fact]
    public void Files_arrive_as_base64_or_a_data_url()
    {
        Assert.Equal([1, 2, 3], AgentTools.DecodeBase64("AQID"));
        Assert.Equal([1, 2, 3], AgentTools.DecodeBase64("data:image/jpeg;base64,AQID"));
        Assert.Equal(400, Assert.Throws<AgentToolException>(() => AgentTools.DecodeBase64("not base64!")).Status);
        Assert.Equal(400, Assert.Throws<AgentToolException>(() => AgentTools.DecodeBase64(new string('A', 14_000_000))).Status);
    }

    [Fact]
    public void Upcoming_payouts_are_the_next_90_days_soonest_first()
    {
        var today = new DateOnly(2026, 11, 11);
        ProjectedPayout P(int days) => new("ASX", "X" + days, today.AddDays(days).ToDateTime(TimeOnly.MinValue), days);
        var list = AgentTools.UpcomingPayouts([P(100), P(5), P(-1), P(0), P(90)], today);
        Assert.Equal(["X0", "X5", "X90"], list.Select(p => p.StockCode));
    }

    [Fact]
    public void Price_history_takes_the_share_from_the_address()
    {
        var tool = new AgentTools().Find("get_price_history");
        var args = ApiEndpoints.Arguments(tool, new Dictionary<string, string> { ["from"] = "2026-01-01" },
            new Dictionary<string, string?> { ["portfolioId"] = "3", ["exchange"] = "ASX", ["stockCode"] = "CBA" }, null);
        Assert.Equal(3L, (long)args["portfolioId"]!);
        Assert.Equal("CBA", (string)args["stockCode"]!);
        Assert.Equal("2026-01-01", (string)args["from"]!);
    }

    [Fact]
    public void Phone_tools_are_reads_so_read_only_keys_get_alerts()
    {
        var tools = new AgentTools();
        Assert.All(new[] { "register_push_device", "list_push_devices", "unregister_push_device", "get_dashboard", "search_securities" },
            n => Assert.False(tools.Find(n)!.Writes, n));
        Assert.True(tools.Find("add_attachment")!.Writes);
    }
}
