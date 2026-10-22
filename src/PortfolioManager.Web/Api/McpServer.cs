using System.Text.Json;
using System.Text.Json.Nodes;
using PortfolioManager.Core.Api;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Api;

/// <summary>
/// A Model Context Protocol server over Streamable HTTP, so an AI assistant such as Claude can use
/// the API's tools. It is stateless: each POST carries one JSON-RPC message and gets its reply as
/// plain JSON. Read-only keys only see the tools that look.
/// </summary>
public sealed class McpServer(AgentTools tools, AgentToolRunner runner, SiteBranding site)
{
    public const string ServerName = "share-portfolio-manager";
    public const string LatestProtocol = "2025-06-18";
    public static readonly string[] Protocols = ["2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05"];

    public const string Instructions =
        "Share Portfolio Manager: share portfolios with double-entry accounts, CGT and tax reports. "
        + "Call list_portfolios first: every other tool needs a portfolioId from it. "
        + "Changes (add_trade, add_cash_transaction, import_bank_transactions, allocate_bank_transaction, match_bank_transaction) "
        + "go through the same rules as the web site and are logged. Dates are yyyy-mm-dd. "
        + "To code bank transactions: import_bank_transactions, then list_unallocated, then find_bank_transaction_matches "
        + "and match_bank_transaction, or list_ledger_accounts and allocate_bank_transaction.";

    public async Task<IResult> HandleAsync(HttpContext http)
    {
        JsonNode? message;
        try
        {
            message = await JsonNode.ParseAsync(http.Request.Body);
        }
        catch (JsonException)
        {
            return Results.Json(Error(null, -32700, "The request isn't valid JSON."));
        }
        if (message is not JsonObject request)
            return Results.Json(Error(null, -32600, "Send one JSON-RPC message per request."));

        var reply = await ReplyAsync(ApiKeyAuthenticationHandler.Caller(http.User), request);
        // A notification or a reply from the client gets no answer
        return reply is null ? Results.Accepted() : Results.Text(reply.ToJsonString(), "application/json");
    }

    /// <summary>The JSON-RPC reply to one message, or null when none is due (a notification).</summary>
    public async Task<JsonObject?> ReplyAsync(ApiCaller caller, JsonObject request)
    {
        var id = request["id"]?.DeepClone();
        var method = request["method"]?.GetValue<string>();
        if (method is null)
            return id is null ? null : Error(id, -32600, "The message has no method.");
        if (id is null)
            return null;

        var parameters = request["params"] as JsonObject;
        return method switch
        {
            "initialize" => Result(id, Initialize(parameters, site.Name)),
            "ping" => Result(id, new JsonObject()),
            "tools/list" => Result(id, new JsonObject { ["tools"] = ListTools(caller) }),
            "tools/call" => await CallAsync(caller, id, parameters),
            _ => Error(id, -32601, $"This server doesn't support {method}."),
        };
    }

    public static JsonObject Initialize(JsonObject? parameters, string title = SiteBranding.DefaultName)
    {
        var asked = parameters?["protocolVersion"]?.GetValue<string>();
        return new JsonObject
        {
            ["protocolVersion"] = asked is not null && Protocols.Contains(asked) ? asked : LatestProtocol,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = ServerName,
                ["title"] = title,
                ["version"] = typeof(McpServer).Assembly.GetName().Version?.ToString() ?? "1.0",
            },
            ["instructions"] = Instructions,
        };
    }

    public JsonArray ListTools(ApiCaller caller) => new(tools.All
        .Where(t => !(t.Writes && caller.ReadOnly))
        .Select(t => (JsonNode)new JsonObject
        {
            ["name"] = t.Name,
            ["title"] = t.Title,
            ["description"] = t.Description,
            ["inputSchema"] = t.InputSchema.DeepClone(),
            ["annotations"] = new JsonObject
            {
                ["title"] = t.Title,
                ["readOnlyHint"] = !t.Writes,
                ["destructiveHint"] = false,
                ["idempotentHint"] = !t.Writes,
                ["openWorldHint"] = false,
            },
        })
        .ToArray());

    private async Task<JsonObject> CallAsync(ApiCaller caller, JsonNode id, JsonObject? parameters)
    {
        var name = parameters?["name"]?.GetValue<string>();
        if (name is null || tools.Find(name) is null)
            return Error(id, -32602, $"Unknown tool: {name}");

        var args = parameters!["arguments"] is JsonObject a ? JsonSerializer.SerializeToElement(a) : JsonSerializer.SerializeToElement(new JsonObject());
        var result = await runner.RunAsync(caller, name, args);
        return Result(id, ToolResult(result));
    }

    /// <summary>A tool's answer as MCP content: the JSON as text, and as structured content when it is an object.</summary>
    public static JsonObject ToolResult(AgentResult result)
    {
        if (!result.Ok)
        {
            var text = result.Error!.Fields is { Count: > 0 } fields
                ? result.Error.Error + " " + string.Join(" ", fields.Select(f => $"[{f.Key}] {f.Value}"))
                : result.Error.Error;
            return new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
                ["isError"] = true,
            };
        }

        var node = JsonSerializer.SerializeToNode(result.Data, AgentJson.Options);
        var structured = node as JsonObject ?? new JsonObject { ["items"] = node };
        return new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = node?.ToJsonString(AgentJson.Options) ?? "null" }),
            ["structuredContent"] = structured,
            ["isError"] = false,
        };
    }

    private static JsonObject Result(JsonNode id, JsonNode result) => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

    public static JsonObject Error(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };
}
