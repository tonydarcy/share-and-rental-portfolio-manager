using System.Text.Json;
using System.Text.Json.Nodes;
using PortfolioManager.Core.Api;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Web.Api;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Tests;

public class ApiTests
{
    private static readonly ApiCaller Editor = new(7, 42, "Claude", ReadOnly: false, "tony@example.com");
    private static readonly ApiCaller Looker = Editor with { ReadOnly = true };
    private static readonly AgentTools Tools = new();

    [Fact]
    public void New_keys_are_long_random_and_marked()
    {
        var a = ApiKeyService.Generate();
        var b = ApiKeyService.Generate();
        Assert.StartsWith("pm_", a);
        Assert.Equal(46, a.Length);
        Assert.NotEqual(a, b);
        Assert.DoesNotContain('+', a + b);
        Assert.DoesNotContain('/', a + b);
        Assert.True(ApiKeyService.LooksLikeKey(a));
        Assert.Equal(a[..10], ApiKeyService.Prefix(a));
    }

    [Fact]
    public void Only_a_hash_of_the_key_is_kept()
    {
        var key = ApiKeyService.Generate();
        var hash = ApiKeyService.Hash(key);
        Assert.Equal(64, hash.Length);
        Assert.Matches("^[0-9a-f]{64}$", hash);
        Assert.Equal(hash, ApiKeyService.Hash(" " + key + " "));
        Assert.DoesNotContain(key[3..], hash);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("pm_short")]
    [InlineData("sk_0123456789012345678901234567890123456789")]
    public void Text_that_cant_be_a_key_never_reaches_the_database(string? key) => Assert.False(ApiKeyService.LooksLikeKey(key));

    [Fact]
    public void A_key_needs_a_short_name()
    {
        Assert.NotNull(ApiKeyService.ValidateName(" "));
        Assert.NotNull(ApiKeyService.ValidateName(new string('x', 101)));
        Assert.Null(ApiKeyService.ValidateName("Claude on my laptop"));
    }

    [Fact]
    public void Keys_map_from_the_proc()
    {
        var key = ApiKeyService.MapKey(new Dictionary<string, object?>
        {
            ["iApiKeyID"] = 3, ["strName"] = " Claude ", ["strKeyPrefix"] = "pm_abcdefg", ["bReadOnly"] = true,
            ["dtmCreatedDate"] = new DateTime(2026, 10, 20), ["dtmLastUsedDate"] = null, ["dtmRevokedDate"] = new DateTime(2026, 10, 21),
        });
        Assert.Equal(new ApiKeyInfo(3, "Claude", "pm_abcdefg", true, new DateTime(2026, 10, 20), null, new DateTime(2026, 10, 21)), key);
        Assert.False(key.Active);

        var caller = ApiKeyService.MapCaller(new Dictionary<string, object?>
        {
            ["iApiKeyID"] = 7, ["iUserID"] = 42, ["strName"] = "Claude", ["bReadOnly"] = false, ["strLoginEmail"] = "tony@example.com",
        });
        Assert.Equal(Editor, caller);
    }

    [Fact]
    public void The_change_log_cuts_long_details()
    {
        var p = ApiAuditService.Parameters(Editor, 99, "add_trade", new string('x', 5000), false, "No");
        object? Get(string name) => p.GetType().GetProperty(name)!.GetValue(p);
        Assert.Equal(ApiAuditService.MaxDetail, ((string)Get("strDetail")!).Length);
        Assert.Equal(7, Get("iApiKeyID"));
        Assert.Equal(42, Get("iUserId"));
        Assert.Equal(99, Get("iPortfolioId"));
        Assert.Equal(false, Get("bSucceeded"));
    }

    [Theory]
    [InlineData("Bearer pm_abc", "", "pm_abc")]
    [InlineData("bearer  pm_abc ", "", "pm_abc")]
    [InlineData("", "pm_xyz", "pm_xyz")]
    [InlineData("Basic dXNlcjpwYXNz", "pm_xyz", "pm_xyz")]
    [InlineData("", "", null)]
    public void The_key_comes_from_either_header(string authorization, string apiKey, string? expected) =>
        Assert.Equal(expected, ApiKeyAuthenticationHandler.ReadKey(authorization, apiKey));

    [Fact]
    public void Claims_round_trip_to_the_caller()
    {
        var user = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(ApiKeyAuthenticationHandler.Claims(Looker), "ApiKey"));
        Assert.Equal(Looker, ApiKeyAuthenticationHandler.Caller(user));
    }

    [Fact]
    public void Changes_need_edit_access_and_a_key_that_isnt_read_only()
    {
        var addTrade = Tools.Find("add_trade")!;
        var listTrades = Tools.Find("list_trades")!;
        var owned = new PortfolioSummary(42, "Mine", Access: PortfolioAccess.Full);
        var edit = new PortfolioSummary(43, "Client", Access: PortfolioAccess.Edit);
        var view = new PortfolioSummary(44, "Audit", Access: PortfolioAccess.View);

        Assert.Null(AgentToolRunner.Refusal(addTrade, Editor, owned));
        Assert.Null(AgentToolRunner.Refusal(addTrade, Editor, edit));
        Assert.Equal(ViewOnlyFilter.Message, AgentToolRunner.Refusal(addTrade, Editor, view));
        Assert.Equal(AgentToolRunner.ReadOnlyKeyMessage, AgentToolRunner.Refusal(addTrade, Looker, owned));
        Assert.Null(AgentToolRunner.Refusal(listTrades, Looker, view));
        Assert.False(AgentToolRunner.CanEdit(Looker, owned));
        Assert.True(AgentToolRunner.CanEdit(Editor, edit));
    }

    [Fact]
    public void Every_tool_has_a_unique_name_and_asks_for_its_portfolio()
    {
        Assert.Equal(Tools.All.Count, Tools.All.Select(t => t.Name).Distinct().Count());
        foreach (var tool in Tools.All.Where(t => t.NeedsPortfolio))
        {
            var required = tool.InputSchema["required"]!.AsArray().Select(n => n!.GetValue<string>());
            Assert.Contains("portfolioId", required);
        }
        Assert.Contains(Tools.All, t => t.Name == "list_portfolios" && !t.NeedsPortfolio);
        Assert.All(new[] { "add_trade", "add_cash_transaction", "import_bank_transactions", "allocate_bank_transaction", "match_bank_transaction" },
            name => Assert.True(Tools.Find(name)!.Writes));
        Assert.All(Tools.All.Where(t => t.Name.StartsWith("list_") || t.Name.StartsWith("report_") || t.Name.StartsWith("get_")),
            t => Assert.False(t.Writes));
    }

    [Fact]
    public void The_url_wins_over_the_body_and_is_typed_by_the_schema()
    {
        var body = new JsonObject { ["PortfolioId"] = 1, ["stockCode"] = "BHP" };
        var args = ApiEndpoints.Arguments(Tools.Find("list_trades"),
            new Dictionary<string, string> { ["limit"] = "5", ["junk"] = "x" },
            new Dictionary<string, string?> { ["portfolioId"] = "42" }, body);

        Assert.Equal(42, args["portfolioId"]!.GetValue<long>());
        Assert.Equal(5, args["limit"]!.GetValue<long>());
        Assert.Equal("BHP", args["stockCode"]!.GetValue<string>());
        Assert.False(args.ContainsKey("PortfolioId"));
        Assert.False(args.ContainsKey("junk"));

        var holdings = ApiEndpoints.Arguments(Tools.Find("get_holdings"), new Dictionary<string, string> { ["openOnly"] = "false" },
            new Dictionary<string, string?>(), null);
        Assert.False(holdings["openOnly"]!.GetValue<bool>());
    }

    [Fact]
    public void Tool_arguments_bind_to_the_web_forms()
    {
        var args = JsonSerializer.SerializeToElement(new
        {
            portfolioId = 42, date = "2026-10-01", type = "buy", market = "asx", code = "bhp", quantity = "100", price = 45.5,
            currency = "AUD", exchangeRate = 1, recordCash = true,
        });
        var call = new AgentCall(Editor, null, args, null!);
        var form = call.Bind<PortfolioManager.Core.Trades.TradeForm>();
        Assert.Equal(new DateOnly(2026, 10, 1), form.Date);
        Assert.Equal(100m, form.Quantity);
        Assert.Equal(45.5m, form.Price);
        Assert.True(form.RecordCash);
        Assert.Equal(42, call.Int("PORTFOLIOID"));
        Assert.True(call.Bool("recordCash", false));
        Assert.Null(call.Int("missing"));

        var bad = new AgentCall(Editor, null, JsonSerializer.SerializeToElement(new { date = "1 Oct 2026" }), null!);
        var ex = Assert.Throws<AgentToolException>(() => bad.Bind<PortfolioManager.Core.Trades.TradeForm>());
        Assert.Equal(400, ex.Status);
        Assert.Throws<AgentToolException>(() => bad.Date("date"));
    }

    [Fact]
    public void Imported_lines_are_checked()
    {
        var today = new DateOnly(2026, 10, 20);
        var (lines, errors) = AgentTools.CheckImport(
        [
            new() { Date = new DateOnly(2026, 10, 1), Description = "  Interest   paid ", Amount = 12.34m },
            new() { Date = new DateOnly(2026, 10, 21), Description = "Future", Amount = 1 },
            new() { Date = new DateOnly(2026, 10, 2), Description = "Zero", Amount = 0 },
            new() { Date = new DateOnly(2026, 10, 2), Description = "Fraction", Amount = 1.234m },
            new() { Date = new DateOnly(2026, 10, 2), Description = " ", Amount = -5 },
        ], today);

        Assert.Single(lines);
        Assert.Equal("Interest paid", lines[0].Description);
        Assert.Equal(4, errors.Count);
        Assert.StartsWith("Transaction 2:", errors[0]);
        Assert.NotEmpty(AgentTools.CheckImport([], today).Errors);
    }

    private static McpServer Mcp() => new(Tools, null!, new SiteBranding { Name = "Smith Portfolios" });

    private static JsonObject Request(string method, JsonObject? parameters = null, int? id = 1)
    {
        var request = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        if (id is not null)
            request["id"] = id;
        if (parameters is not null)
            request["params"] = parameters;
        return request;
    }

    [Fact]
    public async Task Mcp_initialize_agrees_a_protocol_version()
    {
        var reply = await Mcp().ReplyAsync(Editor, Request("initialize", new JsonObject { ["protocolVersion"] = "2025-03-26" }));
        Assert.Equal("2025-03-26", reply!["result"]!["protocolVersion"]!.GetValue<string>());
        // The site's own name (Site:Name in appsettings), not the project's
        Assert.Equal("Smith Portfolios", reply["result"]!["serverInfo"]!["title"]!.GetValue<string>());
        Assert.NotNull(reply["result"]!["capabilities"]!["tools"]);
        Assert.Equal(McpServer.ServerName, reply["result"]!["serverInfo"]!["name"]!.GetValue<string>());

        var unknown = await Mcp().ReplyAsync(Editor, Request("initialize", new JsonObject { ["protocolVersion"] = "1999-01-01" }));
        Assert.Equal(McpServer.LatestProtocol, unknown!["result"]!["protocolVersion"]!.GetValue<string>());
    }

    [Fact]
    public async Task Mcp_notifications_get_no_reply_and_unknown_methods_an_error()
    {
        Assert.Null(await Mcp().ReplyAsync(Editor, Request("notifications/initialized", id: null)));
        var reply = await Mcp().ReplyAsync(Editor, Request("resources/list"));
        Assert.Equal(-32601, reply!["error"]!["code"]!.GetValue<int>());
        var ping = await Mcp().ReplyAsync(Editor, Request("ping"));
        Assert.NotNull(ping!["result"]);
    }

    [Fact]
    public async Task Read_only_keys_only_see_tools_that_look()
    {
        var all = (await Mcp().ReplyAsync(Editor, Request("tools/list")))!["result"]!["tools"]!.AsArray();
        var looking = (await Mcp().ReplyAsync(Looker, Request("tools/list")))!["result"]!["tools"]!.AsArray();

        Assert.Equal(Tools.All.Count, all.Count);
        Assert.Contains(all, t => t!["name"]!.GetValue<string>() == "add_trade");
        Assert.DoesNotContain(looking, t => t!["name"]!.GetValue<string>() == "add_trade");
        Assert.All(looking, t => Assert.True(t!["annotations"]!["readOnlyHint"]!.GetValue<bool>()));
        Assert.Equal("object", all[0]!["inputSchema"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task Mcp_rejects_unknown_tools()
    {
        var reply = await Mcp().ReplyAsync(Editor, Request("tools/call", new JsonObject { ["name"] = "drop_database" }));
        Assert.Equal(-32602, reply!["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public void Tool_results_become_mcp_content()
    {
        var ok = McpServer.ToolResult(new AgentResult(200, new[] { new { PortfolioId = 1 } }, null));
        Assert.False(ok["isError"]!.GetValue<bool>());
        Assert.Contains("\"portfolioId\":1", ok["content"]![0]!["text"]!.GetValue<string>());
        Assert.NotNull(ok["structuredContent"]!["items"]);

        var refused = McpServer.ToolResult(new AgentResult(400, null, new ApiError("Enter the stock code.", new Dictionary<string, string> { ["code"] = "Enter the stock code." })));
        Assert.True(refused["isError"]!.GetValue<bool>());
        Assert.Contains("[code]", refused["content"]![0]!["text"]!.GetValue<string>());
    }
}
