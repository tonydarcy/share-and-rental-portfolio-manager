using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using Microsoft.AspNetCore.DataProtection;
using PortfolioManager.Core.Api;
using PortfolioManager.Core.Assistant;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Core.Tax;
using PortfolioManager.Web.Api;

namespace PortfolioManager.Web.Services;

/// <summary>What happened on a turn: the conversation, whether changes wait for approval, and any problem to show.</summary>
public sealed record AssistantTurn(AssistantTranscript Transcript, IReadOnlyList<AssistantToolUse> Waiting, string? Error = null);

/// <summary>
/// The assistant in the site. It talks to Claude with the person's own Anthropic API key (or the host's, when set),
/// using the same tools as the API and MCP server on the portfolio the person is looking at. Tools that only read run
/// straight away; a tool that changes anything waits until the person approves it on the page. Every change goes
/// through AgentToolRunner, so the access checks and the change log are the same as for an API key.
/// </summary>
public sealed class AssistantChat(AgentTools tools, AgentToolRunner runner, AssistantService store, IDataProtectionProvider protection,
    IConfiguration config, ILogger<AssistantChat> logger)
{
    /// <summary>Tool rounds in one turn before the assistant has to stop and answer.</summary>
    public const int MaxRounds = 10;

    public const int MaxTokens = 8000;

    public const string KeyName = "Site assistant";

    private IDataProtector Protector => protection.CreateProtector("PortfolioManager.Assistant.ApiKey");

    /// <summary>The host's key from settings (Assistant:AnthropicApiKey), used by anyone without their own.</summary>
    public string? HostKey => config["Assistant:AnthropicApiKey"] is { Length: > 0 } k ? k : null;

    public string Protect(string apiKey) => Protector.Protect(apiKey);

    /// <summary>The key to use for this login: their own, else the host's. Null when there is neither.</summary>
    public async Task<(string? Key, string Model, bool KeyUnreadable)> KeyForAsync(int loginId)
    {
        var settings = await store.GetSettingsAsync(loginId);
        var model = AssistantModels.Pick(settings.Model ?? config["Assistant:Model"]);
        if (settings.ApiKeyProtected is { Length: > 0 } saved)
        {
            try
            {
                return (Protector.Unprotect(saved), model, false);
            }
            catch (CryptographicException)
            {
                // The site's data protection keys changed (see DataProtection:KeysPath in SETUP.md)
                return (HostKey, model, true);
            }
        }
        return (HostKey, model, false);
    }

    public static ApiCaller Caller(int loginId, string email) => new(0, loginId, KeyName, false, email);

    /// <summary>The tools the assistant may use: all of them except list_portfolios, without the portfolioId argument.</summary>
    public IReadOnlyList<AgentTool> Tools => tools.All.Where(t => t.NeedsPortfolio).ToList();

    public async Task<AssistantTurn> SendAsync(ApiCaller caller, PortfolioSummary portfolio, string text, CancellationToken ct)
    {
        var transcript = await store.GetChatAsync(caller.UserId, portfolio.PortfolioId);
        if (transcript.PendingToolUses().Count > 0)
            return new(transcript, transcript.PendingToolUses(), "Approve or reject the changes waiting below first.");
        transcript.AddUserText(text.Trim());
        return await RunAsync(caller, portfolio, transcript, ct);
    }

    /// <summary>The person approved or rejected the changes waiting. Reads in the same reply run either way.</summary>
    public async Task<AssistantTurn> DecideAsync(ApiCaller caller, PortfolioSummary portfolio, bool approve, CancellationToken ct)
    {
        var transcript = await store.GetChatAsync(caller.UserId, portfolio.PortfolioId);
        var pending = transcript.PendingToolUses();
        if (pending.Count == 0)
            return new(transcript, []);
        var results = new List<(string, string, bool)>();
        foreach (var use in pending)
        {
            var tool = tools.Find(use.Name);
            if (tool is { Writes: true } && !approve)
                results.Add((use.Id, "The person declined this change, so nothing was saved. Ask what they would like instead.", true));
            else
                results.Add(await RunToolAsync(caller, portfolio, use));
        }
        transcript.AddToolResults(results);
        return await RunAsync(caller, portfolio, transcript, ct);
    }

    public Task ClearAsync(int loginId, int portfolioId) => store.ClearChatAsync(loginId, portfolioId);

    private async Task<AssistantTurn> RunAsync(ApiCaller caller, PortfolioSummary portfolio, AssistantTranscript transcript, CancellationToken ct)
    {
        var (key, model, unreadable) = await KeyForAsync(caller.UserId);
        if (key is null)
            return new(Undo(transcript), [], unreadable
                ? "Your saved API key can't be read any more (the site's encryption keys changed). Enter it again under Assistant settings."
                : "Add your Anthropic API key under Assistant settings first.");

        using var client = new AnthropicClient { ApiKey = key, MaxRetries = 2, Timeout = TimeSpan.FromMinutes(3) };
        try
        {
            for (var round = 0; round < MaxRounds; round++)
            {
                var body = Body(model, portfolio, transcript);
                var response = await client.Messages.Create(MessageCreateParams.FromRawUnchecked(Empty, Empty, body), ct);
                if (response.StopReason == StopReason.Refusal)
                    return new(Undo(transcript), [], "The assistant declined to help with that request.");

                transcript.AddAssistant(response.Content.Select(b => JsonNode.Parse(b.Json.GetRawText())!));
                if (response.StopReason != StopReason.ToolUse)
                {
                    await store.SaveChatAsync(caller.UserId, portfolio.PortfolioId, transcript);
                    return new(transcript, [], response.StopReason == StopReason.MaxTokens ? "The reply was cut short. Ask it to carry on, or to be briefer." : null);
                }

                var pending = transcript.PendingToolUses();
                if (pending.Any(u => tools.Find(u.Name) is not { Writes: false }))
                {
                    // A change (or an unknown tool) waits for the person
                    await store.SaveChatAsync(caller.UserId, portfolio.PortfolioId, transcript);
                    return new(transcript, pending);
                }
                var results = new List<(string, string, bool)>();
                foreach (var use in pending)
                    results.Add(await RunToolAsync(caller, portfolio, use));
                transcript.AddToolResults(results);
            }
            await store.SaveChatAsync(caller.UserId, portfolio.PortfolioId, transcript);
            return new(transcript, [], "The assistant used a lot of steps without finishing. Ask it to carry on, or ask something narrower.");
        }
        catch (AnthropicUnauthorizedException)
        {
            return new(Undo(transcript), [], "Anthropic refused the API key. Check it under Assistant settings.");
        }
        catch (AnthropicRateLimitException)
        {
            return new(Undo(transcript), [], "Anthropic says the key has hit its limit for now. Wait a minute and try again.");
        }
        catch (AnthropicBadRequestException ex) when (ex.Message.Contains("credit", StringComparison.OrdinalIgnoreCase))
        {
            return new(Undo(transcript), [], "The Anthropic account has no credit left. Add some at console.anthropic.com.");
        }
        catch (AnthropicException ex)
        {
            logger.LogWarning(ex, "Assistant call failed for login {LoginId}", caller.UserId);
            return new(Undo(transcript), [], "The assistant couldn't be reached just now. Try again in a moment.");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(Undo(transcript), [], "The assistant took too long to answer. Try again, or ask something narrower.");
        }
    }

    /// <summary>A failed turn: back to how it was before the person's last message (saved), so the conversation stays valid.</summary>
    private static AssistantTranscript Undo(AssistantTranscript transcript)
    {
        if (transcript.PendingToolUses().Count == 0)
            transcript.RemoveLastExchange();
        return transcript;
    }

    private async Task<(string, string, bool)> RunToolAsync(ApiCaller caller, PortfolioSummary portfolio, AssistantToolUse use)
    {
        if (tools.Find(use.Name) is not { NeedsPortfolio: true })
            return (use.Id, $"There is no tool called {use.Name}.", true);
        var args = JsonSerializer.SerializeToElement(AssistantSchemas.WithPortfolio(use.Input, portfolio.PortfolioId));
        var result = await runner.RunAsync(caller, use.Name, args);
        return result.Ok
            ? (use.Id, AssistantSchemas.Cut(JsonSerializer.Serialize(result.Data, AgentJson.Options)), false)
            : (use.Id, result.Error?.Error ?? "The tool failed.", true);
    }

    private static readonly Dictionary<string, JsonElement> Empty = [];

    private Dictionary<string, JsonElement> Body(string model, PortfolioSummary portfolio, AssistantTranscript transcript)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var region = Core.Regions.RegionSettings.Current;
        var country = Core.Regions.RegionSettings.Countries.FirstOrDefault(c => c.Code == region.CountryCode)?.Name ?? region.CountryCode;
        var system = $"""
            You are the assistant in Share Portfolio Manager, a web site for share portfolios, rental properties, accounting and tax.
            You are helping with the portfolio "{portfolio.Name}" ({(portfolio.CanEdit ? "the person can change it" : "the person can only view it")}).
            Today is {today:d MMMM yyyy}. The portfolio's country is {country}, its currency {region.Currency}, and the current financial year is {FinancialYear.Label(FinancialYear.EndYearFor(today, region.YearStart), region.YearStart)}.

            Use the tools to look things up rather than guessing, and quote figures as the tools return them. Every tool works on this portfolio only.
            Tools that change anything don't run until the person approves them on the page, so when a change is wanted, call the tool with the details
            rather than asking for permission in words first; say briefly what you are about to do. If a change is declined, ask what they would like instead.
            Write in plain English for someone who isn't an accountant, and keep answers short. You can explain tax rules in general terms, but say when
            something needs a registered tax agent's advice.
            """;
        var toolList = new JsonArray(Tools.Select(t => (JsonNode)new JsonObject
        {
            ["name"] = t.Name,
            ["description"] = t.Writes ? t.Description + " (Changes the portfolio: the person approves it first.)" : t.Description,
            ["input_schema"] = AssistantSchemas.WithoutPortfolio(t.InputSchema),
        }).ToArray());
        var body = new JsonObject
        {
            ["model"] = model,
            ["max_tokens"] = MaxTokens,
            ["system"] = system,
            ["thinking"] = new JsonObject { ["type"] = "adaptive" },
            // Caches the tools, instructions and conversation so far, so each round costs less
            ["cache_control"] = new JsonObject { ["type"] = "ephemeral" },
            ["tools"] = toolList,
            ["messages"] = new JsonArray(transcript.Messages.Select(m => (JsonNode)m.DeepClone()).ToArray()),
        };
        return body.Deserialize<Dictionary<string, JsonElement>>()!;
    }
}
