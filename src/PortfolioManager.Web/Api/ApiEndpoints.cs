using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;

namespace PortfolioManager.Web.Api;

/// <summary>
/// The JSON API (/api/v1/...) and the MCP server (/mcp). Both sign in only with an API key and run
/// the same tools (AgentTools), so an AI agent and a script or app get the same answers and rules.
/// </summary>
public static class ApiEndpoints
{
    public const string Policy = "ApiKey";
    public const string RateLimit = "api";

    public static void MapAgentApi(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1").RequireAuthorization(Policy).RequireRateLimiting(RateLimit).DisableAntiforgery();

        api.MapGet("/tools", (AgentTools tools) => tools.All.Select(t => new { t.Name, t.Title, t.Description, t.Writes, t.InputSchema }));
        // Any tool by name, with its arguments as the body (the URL adds nothing)
        api.MapPost("/tools/{name}", async (string name, HttpContext http) => await RunAsync(http, name, await ReadBodyAsync(http), fromUrl: false));

        Get(api, "/portfolios", "list_portfolios");
        Get(api, "/portfolios/{portfolioId:int}/holdings", "get_holdings");
        Get(api, "/portfolios/{portfolioId:int}/trades", "list_trades");
        Post(api, "/portfolios/{portfolioId:int}/trades", "add_trade");
        Get(api, "/portfolios/{portfolioId:int}/cash", "list_cash_transactions");
        Post(api, "/portfolios/{portfolioId:int}/cash", "add_cash_transaction");
        Get(api, "/portfolios/{portfolioId:int}/bank-accounts", "list_bank_accounts");
        Get(api, "/portfolios/{portfolioId:int}/ledger-accounts", "list_ledger_accounts");
        Get(api, "/portfolios/{portfolioId:int}/tax-codes", "list_tax_codes");
        Post(api, "/portfolios/{portfolioId:int}/bank-imports", "import_bank_transactions");
        Get(api, "/portfolios/{portfolioId:int}/unallocated", "list_unallocated");
        Get(api, "/portfolios/{portfolioId:int}/unallocated/{lineId:int}/matches", "find_bank_transaction_matches");
        Post(api, "/portfolios/{portfolioId:int}/unallocated/{lineId:int}/allocate", "allocate_bank_transaction");
        Post(api, "/portfolios/{portfolioId:int}/unallocated/{lineId:int}/match", "match_bank_transaction");
        Get(api, "/portfolios/{portfolioId:int}/invoices", "list_invoices");
        Get(api, "/portfolios/{portfolioId:int}/invoices/{invoiceId:int}", "get_invoice");
        Get(api, "/portfolios/{portfolioId:int}/reports/capital-gains", "report_capital_gains");
        Get(api, "/portfolios/{portfolioId:int}/reports/taxable-income", "report_taxable_income");
        Get(api, "/portfolios/{portfolioId:int}/reports/unrealised-gains", "report_unrealised_gains");
        Get(api, "/portfolios/{portfolioId:int}/reports/trial-balance", "report_trial_balance");
        Get(api, "/portfolios/{portfolioId:int}/reports/profit-and-loss", "report_profit_and_loss");
        Get(api, "/portfolios/{portfolioId:int}/reports/balance-sheet", "report_balance_sheet");

        var mcp = app.MapGroup("/mcp").RequireAuthorization(Policy).RequireRateLimiting(RateLimit).DisableAntiforgery();
        mcp.MapPost("", async (HttpContext http, McpServer server) => await server.HandleAsync(http));
        // No server-to-client stream or sessions: every reply comes back on the POST
        mcp.MapGet("", () => Results.StatusCode(StatusCodes.Status405MethodNotAllowed));
        mcp.MapDelete("", () => Results.StatusCode(StatusCodes.Status405MethodNotAllowed));
    }

    /// <summary>The policy for the API: signed in with an API key, never the sign-in cookie.</summary>
    public static AuthorizationPolicy BuildPolicy() => new AuthorizationPolicyBuilder(ApiKeyAuthenticationHandler.SchemeName)
        .RequireAuthenticatedUser()
        .RequireClaim(ApiKeyAuthenticationHandler.KeyIdClaim)
        .Build();

    private static void Get(RouteGroupBuilder api, string pattern, string tool) =>
        api.MapGet(pattern, async Task<IResult> (HttpContext http) => await RunAsync(http, tool, null));

    private static void Post(RouteGroupBuilder api, string pattern, string tool) =>
        api.MapPost(pattern, async Task<IResult> (HttpContext http) => await RunAsync(http, tool, await ReadBodyAsync(http)));

    private static async Task<IResult> RunAsync(HttpContext http, string toolName, JsonObject? body, bool fromUrl = true)
    {
        var tools = http.RequestServices.GetRequiredService<AgentTools>();
        var runner = http.RequestServices.GetRequiredService<AgentToolRunner>();
        if (body is null && http.Request.ContentLength > 0)
            return Results.Json(new ApiError("Send the request body as a JSON object."), statusCode: 400);

        var tool = tools.Find(toolName);
        var args = fromUrl
            ? Arguments(tool, http.Request.Query.ToDictionary(q => q.Key, q => q.Value.ToString()),
                http.Request.RouteValues.ToDictionary(r => r.Key, r => r.Value?.ToString()), body)
            : body ?? [];
        var result = await runner.RunAsync(ApiKeyAuthenticationHandler.Caller(http.User), toolName, JsonSerializer.SerializeToElement(args));
        return result.Ok
            ? Results.Json(result.Data, AgentJson.Options)
            : Results.Json(result.Error, AgentJson.Options, statusCode: result.Status);
    }

    /// <summary>
    /// One set of arguments from the body, the query string and the route (the route wins, so a body
    /// can't point a call at another portfolio). Text from the URL is turned into the number or
    /// true/false the tool's schema asks for.
    /// </summary>
    public static JsonObject Arguments(AgentTool? tool, IReadOnlyDictionary<string, string> query, IReadOnlyDictionary<string, string?> route, JsonObject? body)
    {
        var args = body?.DeepClone().AsObject() ?? [];
        var properties = tool?.InputSchema["properties"] as JsonObject;
        foreach (var (key, value) in query.Select(q => (q.Key, (string?)q.Value)).Concat(route.Select(r => (r.Key, r.Value))))
        {
            if (value is null)
                continue;
            var name = properties?.Select(p => p.Key).FirstOrDefault(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
            if (name is null)
                continue;
            foreach (var existing in args.Select(a => a.Key).Where(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase)).ToList())
                args.Remove(existing);
            args[name] = Typed(properties![name]?["type"]?.GetValue<string>(), value);
        }
        return args;
    }

    private static JsonNode? Typed(string? type, string value) => type switch
    {
        "integer" when long.TryParse(value, out var n) => n,
        "number" when decimal.TryParse(value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var d) => d,
        "boolean" when bool.TryParse(value, out var b) => b,
        _ => value,
    };

    private static async Task<JsonObject?> ReadBodyAsync(HttpContext http)
    {
        if (http.Request.ContentLength is 0 || !(http.Request.HasJsonContentType() || http.Request.ContentType is null))
            return null;
        try
        {
            return await JsonNode.ParseAsync(http.Request.Body) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
