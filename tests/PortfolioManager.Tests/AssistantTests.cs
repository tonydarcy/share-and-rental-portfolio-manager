using System.Text.Json.Nodes;
using PortfolioManager.Core.Assistant;
using PortfolioManager.Web.Api;

namespace PortfolioManager.Tests;

public class AssistantTests
{
    private static JsonNode Block(string json) => JsonNode.Parse(json)!;

    private static AssistantTranscript WithToolUse()
    {
        var t = AssistantTranscript.Empty();
        t.AddUserText("Buy 100 BHP");
        t.AddAssistant([
            Block("""{"type":"thinking","thinking":"hm","signature":"sig1"}"""),
            Block("""{"type":"text","text":"I'll record that."}"""),
            Block("""{"type":"tool_use","id":"tu_1","name":"add_trade","input":{"code":"BHP","quantity":100}}"""),
        ]);
        return t;
    }

    [Fact]
    public void Pending_tool_uses_are_the_last_replys_tools()
    {
        var t = WithToolUse();
        var pending = Assert.Single(t.PendingToolUses());
        Assert.Equal("tu_1", pending.Id);
        Assert.Equal("add_trade", pending.Name);
        Assert.Equal("BHP", (string?)pending.Input["code"]);

        t.AddToolResults([("tu_1", "{\"ok\":true}", false)]);
        Assert.Empty(t.PendingToolUses());
    }

    [Fact]
    public void Round_trips_with_thinking_signatures_kept()
    {
        var json = WithToolUse().Serialize();
        var back = AssistantTranscript.Parse(json);
        Assert.Equal(json, back.Serialize());
        Assert.Contains("\"signature\":\"sig1\"", json);
        Assert.True(AssistantTranscript.Parse("not json").IsEmpty);
        Assert.True(AssistantTranscript.Parse(null).IsEmpty);
    }

    [Fact]
    public void Items_show_text_tools_and_results_but_not_thinking()
    {
        var t = WithToolUse();
        t.AddToolResults([("tu_1", "Locked period", true)]);
        var items = t.Items();
        Assert.Equal([AssistantItem.Said, AssistantItem.Said, AssistantItem.Tool, AssistantItem.Result], items.Select(i => i.Kind));
        Assert.Equal("add_trade", items[3].ToolName);
        Assert.True(items[3].IsError);
        Assert.DoesNotContain(items, i => i.Text.Contains("hm"));
    }

    [Fact]
    public void Remove_last_exchange_goes_back_to_before_the_last_typed_message()
    {
        var t = WithToolUse();
        t.AddToolResults([("tu_1", "done", false)]);
        t.AddUserText("And sell it again");
        t.RemoveLastExchange();
        Assert.Equal(3, t.Messages.Count);
        t.RemoveLastExchange();
        Assert.True(t.IsEmpty);
    }

    [Fact]
    public void Trim_drops_whole_exchanges_from_the_start()
    {
        var t = AssistantTranscript.Empty();
        for (var i = 0; i < 20; i++)
        {
            t.AddUserText(new string('q', 1000));
            t.AddAssistant([Block($$"""{"type":"text","text":"{{new string('a', 1000)}}"}""")]);
        }
        t.Trim(10_000);
        Assert.True(t.Serialize().Length <= 10_000);
        Assert.Equal("user", (string?)t.Messages[0]["role"]);
        Assert.Equal("text", (string?)t.Messages[0]["content"]![0]!["type"]);
    }

    [Fact]
    public void Portfolio_id_is_hidden_from_the_model_and_filled_in_by_the_site()
    {
        var schema = new ToolSchema().Portfolio().String("code", "Code", required: true).Build();
        var forModel = AssistantSchemas.WithoutPortfolio(schema);
        Assert.Null(forModel["properties"]!["portfolioId"]);
        Assert.Equal(["code"], forModel["required"]!.AsArray().Select(n => (string?)n));
        Assert.NotNull(schema["properties"]!["portfolioId"]);

        var noArgs = AssistantSchemas.WithoutPortfolio(new ToolSchema().Portfolio().Build());
        Assert.Null(noArgs["required"]);

        var args = AssistantSchemas.WithPortfolio(new JsonObject { ["PortfolioId"] = 999, ["code"] = "BHP" }, 7);
        Assert.Equal(7, (int?)args["portfolioId"]);
        Assert.Null(args["PortfolioId"]);
    }

    [Fact]
    public void Every_tool_but_list_portfolios_takes_the_portfolio()
    {
        var tools = new AgentTools();
        Assert.All(tools.All.Where(t => t.Name != "list_portfolios"), t => Assert.True(t.NeedsPortfolio, t.Name));
        Assert.NotNull(tools.Find("report_rental_schedule"));
    }

    [Fact]
    public void Long_results_are_cut_and_models_fall_back_to_the_default()
    {
        Assert.Equal("short", AssistantSchemas.Cut("short"));
        Assert.StartsWith(new string('x', 100), AssistantSchemas.Cut(new string('x', 200), 100));
        Assert.Contains("Cut short", AssistantSchemas.Cut(new string('x', 200), 100));
        Assert.Equal(AssistantModels.Default, AssistantModels.Pick("gpt-4"));
        Assert.Equal("claude-haiku-5-5", AssistantModels.Pick("claude-haiku-5-5"));
    }
}
