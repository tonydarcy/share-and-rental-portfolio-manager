using System.Text.Json;
using System.Text.Json.Nodes;

namespace PortfolioManager.Core.Assistant;

/// <summary>The models a person can pick for the assistant, best first.</summary>
public static class AssistantModels
{
    public const string Default = "claude-opus-5-5";

    public static readonly IReadOnlyList<(string Id, string Name)> All =
    [
        ("claude-opus-5-5", "Claude Opus 5.5 (most capable)"),
        ("claude-sonnet-5-5", "Claude Sonnet 5.5 (faster, costs less)"),
        ("claude-haiku-5-5", "Claude Haiku 5.5 (fastest, cheapest)"),
    ];

    public static string Pick(string? model) => All.Any(m => m.Id == model) ? model! : Default;
}

/// <summary>A tool the model asked to run: its id (to send the result back), name and arguments.</summary>
public sealed record AssistantToolUse(string Id, string Name, JsonObject Input);

/// <summary>One thing to show in the chat: something said, a tool the assistant used, or what a tool returned.</summary>
public sealed record AssistantItem(string Role, string Kind, string Text, string? ToolName = null, string? ToolUseId = null, bool IsError = false)
{
    public const string Said = "text", Tool = "tool", Result = "result";
}

/// <summary>
/// The conversation, kept exactly as the messages sent to the Claude API: a list of {role, content: [blocks]}. Keeping
/// the raw blocks means thinking blocks go back with their signatures, as the API needs while tools are being used.
/// </summary>
public sealed class AssistantTranscript
{
    /// <summary>The saved conversation is trimmed (oldest exchanges first) to keep it under this many characters.</summary>
    public const int MaxChars = 400_000;

    private readonly List<JsonObject> _messages;

    private AssistantTranscript(List<JsonObject> messages) => _messages = messages;

    public static AssistantTranscript Empty() => new([]);

    public IReadOnlyList<JsonObject> Messages => _messages;

    public bool IsEmpty => _messages.Count == 0;

    public static AssistantTranscript Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Empty();
        try
        {
            return JsonNode.Parse(json) is JsonArray a ? new(a.OfType<JsonObject>().Select(m => (JsonObject)m.DeepClone()).ToList()) : Empty();
        }
        catch (JsonException)
        {
            return Empty();
        }
    }

    public string Serialize() => new JsonArray(_messages.Select(m => (JsonNode)m.DeepClone()).ToArray()).ToJsonString();

    public void AddUserText(string text) =>
        _messages.Add(new JsonObject { ["role"] = "user", ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }) });

    /// <summary>The assistant's reply, as the raw content blocks the API returned.</summary>
    public void AddAssistant(IEnumerable<JsonNode> blocks) =>
        _messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = new JsonArray(blocks.Select(b => b.DeepClone()).ToArray()) });

    /// <summary>The results of the tools the assistant asked for, all in one user message as the API needs.</summary>
    public void AddToolResults(IEnumerable<(string ToolUseId, string Content, bool IsError)> results)
    {
        var content = new JsonArray();
        foreach (var (id, text, error) in results)
        {
            var block = new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = id, ["content"] = text };
            if (error)
                block["is_error"] = true;
            content.Add(block);
        }
        _messages.Add(new JsonObject { ["role"] = "user", ["content"] = content });
    }

    /// <summary>Takes off everything from the last message the person typed, for a reply that was refused or failed.</summary>
    public void RemoveLastExchange()
    {
        var i = _messages.FindLastIndex(IsTypedByPerson);
        if (i >= 0)
            _messages.RemoveRange(i, _messages.Count - i);
    }

    /// <summary>The tools the assistant asked for in its last reply that have no results yet.</summary>
    public IReadOnlyList<AssistantToolUse> PendingToolUses()
    {
        if (_messages.Count == 0 || (string?)_messages[^1]["role"] != "assistant")
            return [];
        return Blocks(_messages[^1])
            .Where(b => (string?)b["type"] == "tool_use")
            .Select(b => new AssistantToolUse((string?)b["id"] ?? "", (string?)b["name"] ?? "", b["input"] as JsonObject ?? []))
            .ToList();
    }

    /// <summary>Drops the oldest exchanges until the conversation fits; it always starts with a message the person typed.</summary>
    public void Trim(int maxChars = MaxChars)
    {
        while (_messages.Count > 2 && Serialize().Length > maxChars)
        {
            var next = _messages.FindIndex(1, IsTypedByPerson);
            if (next < 0)
                break;
            _messages.RemoveRange(0, next);
        }
    }

    /// <summary>What to show in the chat, in order. Thinking isn't shown.</summary>
    public IReadOnlyList<AssistantItem> Items()
    {
        var items = new List<AssistantItem>();
        var toolNames = new Dictionary<string, string>();
        foreach (var m in _messages)
        {
            var role = (string?)m["role"] ?? "user";
            foreach (var b in Blocks(m))
            {
                switch ((string?)b["type"])
                {
                    case "text" when (string?)b["text"] is { Length: > 0 } text:
                        items.Add(new AssistantItem(role, AssistantItem.Said, text));
                        break;
                    case "tool_use":
                        var id = (string?)b["id"] ?? "";
                        var name = (string?)b["name"] ?? "";
                        toolNames[id] = name;
                        items.Add(new AssistantItem(role, AssistantItem.Tool, (b["input"] ?? new JsonObject()).ToJsonString(Indented), name, id));
                        break;
                    case "tool_result":
                        var useId = (string?)b["tool_use_id"] ?? "";
                        items.Add(new AssistantItem(role, AssistantItem.Result, ResultText(b["content"]), toolNames.GetValueOrDefault(useId), useId,
                            (bool?)b["is_error"] ?? false));
                        break;
                }
            }
        }
        return items;
    }

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static string ResultText(JsonNode? content) => content switch
    {
        JsonValue v => (string?)v ?? "",
        JsonArray a => string.Join("\n", a.OfType<JsonObject>().Select(o => (string?)o["text"]).Where(t => t is not null)),
        _ => "",
    };

    private static IEnumerable<JsonObject> Blocks(JsonObject message) => message["content"] switch
    {
        JsonArray a => a.OfType<JsonObject>(),
        JsonValue v when (string?)v is { } text => [new JsonObject { ["type"] = "text", ["text"] = text }],
        _ => [],
    };

    private static bool IsTypedByPerson(JsonObject m) =>
        (string?)m["role"] == "user" && Blocks(m).Any() && Blocks(m).All(b => (string?)b["type"] == "text");
}

public static class AssistantSchemas
{
    /// <summary>
    /// A tool's input schema for the site assistant: without portfolioId, which the site fills in with the portfolio the
    /// person is looking at, so the assistant can't reach another portfolio.
    /// </summary>
    public static JsonObject WithoutPortfolio(JsonObject schema)
    {
        var copy = (JsonObject)schema.DeepClone();
        if (copy["properties"] is JsonObject props)
            props.Remove("portfolioId");
        if (copy["required"] is JsonArray required)
        {
            foreach (var r in required.Where(r => (string?)r == "portfolioId").ToList())
                required.Remove(r);
            if (required.Count == 0)
                copy.Remove("required");
        }
        return copy;
    }

    /// <summary>The arguments the assistant sent, with the portfolio the person is looking at (replacing any it sent).</summary>
    public static JsonObject WithPortfolio(JsonObject input, int portfolioId)
    {
        var copy = (JsonObject)input.DeepClone();
        foreach (var key in copy.Select(p => p.Key).Where(k => string.Equals(k, "portfolioId", StringComparison.OrdinalIgnoreCase)).ToList())
            copy.Remove(key);
        copy["portfolioId"] = portfolioId;
        return copy;
    }

    /// <summary>A tool's result as text for the model, cut short when it is very long.</summary>
    public static string Cut(string text, int max = 60_000) =>
        text.Length <= max ? text : text[..max] + $"\n[Cut short: the result was {text.Length:N0} characters. Ask for fewer rows, e.g. with a limit or a date range.]";
}
