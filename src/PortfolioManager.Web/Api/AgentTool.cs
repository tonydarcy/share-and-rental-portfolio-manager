using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using PortfolioManager.Core.Api;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Core.Validation;

namespace PortfolioManager.Web.Api;

/// <summary>
/// One thing an agent can do, such as add_trade. The REST endpoints and the MCP server both run
/// these, so access checks, validation and the change log are the same whichever way they're called.
/// </summary>
/// <param name="Writes">Changes the portfolio: needs edit access and a key that isn't read only, and is logged.</param>
/// <param name="NeedsPortfolio">Takes a portfolioId the caller must be able to open.</param>
public sealed record AgentTool(
    string Name,
    string Title,
    string Description,
    bool Writes,
    JsonObject InputSchema,
    Func<AgentCall, Task<object?>> Run,
    bool NeedsPortfolio = true);

/// <summary>A tool being run: who is calling, the portfolio (already checked) and the arguments.</summary>
public sealed class AgentCall(ApiCaller caller, PortfolioSummary? portfolio, JsonElement args, IServiceProvider services)
{
    public ApiCaller Caller { get; } = caller;
    public JsonElement Args { get; } = args;
    public IServiceProvider Services { get; } = services;

    public PortfolioSummary Portfolio => portfolio ?? throw new InvalidOperationException("This tool has no portfolio.");
    public int PortfolioId => Portfolio.PortfolioId;

    public T Service<T>() where T : notnull => Services.GetRequiredService<T>();

    /// <summary>The arguments read into a form class (names match, any case).</summary>
    public T Bind<T>() where T : new()
    {
        try
        {
            return Args.ValueKind == JsonValueKind.Object ? Args.Deserialize<T>(AgentJson.Options) ?? new T() : new T();
        }
        catch (JsonException ex)
        {
            throw new AgentToolException(400, $"An argument isn't in the right format{(ex.Path is { Length: > 2 } p ? $" ({p[2..]})" : "")}. Dates are yyyy-mm-dd and amounts are plain numbers.");
        }
    }

    /// <summary>
    /// For an edit: the stored values with the arguments that match one of their names (any case) laid over them.
    /// Arguments left out, or sent as null, keep the stored value.
    /// </summary>
    public T BindOver<T>(T stored) where T : new()
    {
        var merged = JsonSerializer.SerializeToNode(stored, AgentJson.Options)!.AsObject();
        if (Args.ValueKind == JsonValueKind.Object)
            foreach (var p in Args.EnumerateObject())
            {
                var name = merged.Select(m => m.Key).FirstOrDefault(k => string.Equals(k, p.Name, StringComparison.OrdinalIgnoreCase));
                if (name is not null && p.Value.ValueKind != JsonValueKind.Null)
                    merged[name] = JsonNode.Parse(p.Value.GetRawText());
            }
        try
        {
            return merged.Deserialize<T>(AgentJson.Options) ?? new T();
        }
        catch (JsonException ex)
        {
            throw new AgentToolException(400, $"An argument isn't in the right format{(ex.Path is { Length: > 2 } path ? $" ({path[2..]})" : "")}. Dates are yyyy-mm-dd and amounts are plain numbers.");
        }
    }

    /// <summary>The argument with this name (any case), or null when it's missing or null.</summary>
    public JsonElement? Arg(string name)
    {
        if (Args.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var p in Args.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                return p.Value.ValueKind == JsonValueKind.Null ? null : p.Value;
        return null;
    }

    public string? String(string name) =>
        Arg(name) is { } v ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText())?.Trim() is { Length: > 0 } s ? s : null : null;

    public int? Int(string name) => Arg(name) is { } v ? Read<int?>(v, name) : null;

    public int RequiredInt(string name) => Int(name) ?? throw new AgentToolException(400, $"{name} is required.");

    public decimal? Decimal(string name) => Arg(name) is { } v ? Read<decimal?>(v, name) : null;

    public bool Bool(string name, bool fallback) => Arg(name) is { } v
        ? v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(v.GetString(), out var b) => b,
            _ => throw new AgentToolException(400, $"{name} must be true or false."),
        }
        : fallback;

    public DateOnly? Date(string name)
    {
        var text = String(name);
        if (text is null)
            return null;
        return DateOnly.TryParseExact(text, "yyyy-MM-dd", out var d)
            ? d
            : throw new AgentToolException(400, $"{name} must be a date as yyyy-mm-dd.");
    }

    private static T? Read<T>(JsonElement v, string name)
    {
        try
        {
            return v.Deserialize<T>(AgentJson.Options);
        }
        catch (JsonException)
        {
            throw new AgentToolException(400, $"{name} isn't in the right format.");
        }
    }
}

/// <summary>A tool refused the call: the status and message go back to the agent.</summary>
public sealed class AgentToolException(int status, string message, IReadOnlyDictionary<string, string>? fields = null) : Exception(message)
{
    public int Status { get; } = status;
    public IReadOnlyDictionary<string, string>? Fields { get; } = fields;

    /// <summary>The validation messages of a form, as a 400.</summary>
    public static AgentToolException Invalid(IReadOnlyList<FieldError> errors) => new(400,
        string.Join(" ", errors.Select(e => e.Message)),
        errors.GroupBy(e => AgentJson.CamelCase(e.Field)).ToDictionary(g => g.Key, g => string.Join(" ", g.Select(e => e.Message))));
}

public static class AgentJson
{
    /// <summary>camelCase names, enums as text, numbers accepted in quotes.</summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        // Plain quotes and apostrophes: the API only ever sends JSON, never HTML
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string CamelCase(string name) => string.IsNullOrEmpty(name) ? name : char.ToLowerInvariant(name[0]) + name[1..];
}

/// <summary>Builds the JSON Schema an MCP client shows for a tool's arguments.</summary>
public sealed class ToolSchema
{
    private readonly JsonObject _properties = [];
    private readonly JsonArray _required = [];

    public ToolSchema Portfolio() => Int("portfolioId", "The portfolio, from list_portfolios.", required: true);

    public ToolSchema String(string name, string description, bool required = false, string? format = null, params string[] choices)
    {
        var p = new JsonObject { ["type"] = "string", ["description"] = description };
        if (format is not null)
            p["format"] = format;
        if (choices.Length > 0)
            p["enum"] = new JsonArray(choices.Select(c => (JsonNode)JsonValue.Create(c)).ToArray());
        return Add(name, p, required);
    }

    public ToolSchema Date(string name, string description, bool required = false) => String(name, description + " (yyyy-mm-dd)", required, "date");

    public ToolSchema Int(string name, string description, bool required = false) =>
        Add(name, new JsonObject { ["type"] = "integer", ["description"] = description }, required);

    public ToolSchema Number(string name, string description, bool required = false) =>
        Add(name, new JsonObject { ["type"] = "number", ["description"] = description }, required);

    public ToolSchema Bool(string name, string description, bool required = false) =>
        Add(name, new JsonObject { ["type"] = "boolean", ["description"] = description }, required);

    public ToolSchema Array(string name, string description, ToolSchema items, bool required = false) =>
        Add(name, new JsonObject { ["type"] = "array", ["description"] = description, ["items"] = items.Build() }, required);

    public ToolSchema Object(string name, string description, ToolSchema properties, bool required = false)
    {
        var p = properties.Build();
        p["description"] = description;
        return Add(name, p, required);
    }

    public JsonObject Build()
    {
        var schema = new JsonObject { ["type"] = "object", ["properties"] = _properties.DeepClone() };
        if (_required.Count > 0)
            schema["required"] = _required.DeepClone();
        return schema;
    }

    private ToolSchema Add(string name, JsonObject property, bool required)
    {
        _properties[name] = property;
        if (required)
            _required.Add(name);
        return this;
    }
}
