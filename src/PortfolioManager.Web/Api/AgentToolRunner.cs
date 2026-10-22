using System.Globalization;
using System.Text.Json;
using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Api;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Core.Regions;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Api;

/// <summary>What a tool call returned: Data on success, otherwise the HTTP status and why.</summary>
public sealed record AgentResult(int Status, object? Data, ApiError? Error)
{
    public bool Ok => Error is null;
}

/// <summary>
/// Runs a tool for an API key: checks the key's login can open the portfolio, that a change is
/// allowed (edit access and a key that isn't read only), sets the portfolio's region, and writes
/// every change, or refused change, to the agent change log.
/// </summary>
public sealed class AgentToolRunner(AgentTools tools, PortfolioService portfolios, ApiAuditService audit, IServiceProvider services, ILogger<AgentToolRunner> logger)
{
    public const string ReadOnlyKeyMessage = "This API key is read only. Make a key without \"Read only\" ticked to make changes.";

    public async Task<AgentResult> RunAsync(ApiCaller caller, string toolName, JsonElement args)
    {
        if (tools.Find(toolName) is not { } tool)
            return Fail(404, $"There is no tool called {toolName}.");

        PortfolioSummary? portfolio = null;
        int? portfolioId = null;
        try
        {
            if (tool.NeedsPortfolio)
            {
                var probe = new AgentCall(caller, null, args, services);
                portfolioId = probe.Int("portfolioId") ?? throw new AgentToolException(400, "portfolioId is required. list_portfolios gives the ids.");
                portfolio = (await portfolios.GetAccessibleAsync(caller.UserId)).FirstOrDefault(p => p.PortfolioId == portfolioId);
                if (portfolio is null)
                    throw new AgentToolException(404, "There is no portfolio with that id that this key's login can open.");
                if (Refusal(tool, caller, portfolio) is { } refusal)
                    throw new AgentToolException(403, refusal);

                var region = await portfolios.GetRegionAsync(portfolio.PortfolioId);
                RegionSettings.Current = region;
                CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = region.Culture();
            }

            var data = await tool.Run(new AgentCall(caller, portfolio, args, services));
            await LogAsync(tool, caller, portfolioId, args, true, "Done");
            return new AgentResult(200, data, null);
        }
        catch (AgentToolException ex)
        {
            await LogAsync(tool, caller, portfolioId, args, false, ex.Message);
            return new AgentResult(ex.Status, null, new ApiError(ex.Message, ex.Fields));
        }
        catch (Exception ex) when (LockedPeriodService.IsLockError(ex))
        {
            var message = LockedPeriodFilter.Message(ex);
            await LogAsync(tool, caller, portfolioId, args, false, message);
            return Fail(409, message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "API tool {Tool} failed for key {KeyId}, portfolio {PortfolioId}", tool.Name, caller.ApiKeyId, portfolioId);
            const string message = "Something went wrong and nothing was saved. Try again, or do it on the web site.";
            await LogAsync(tool, caller, portfolioId, args, false, message);
            return Fail(500, message);
        }
    }

    /// <summary>Why the key may not run the tool on the portfolio, or null when it may.</summary>
    public static string? Refusal(AgentTool tool, ApiCaller caller, PortfolioSummary portfolio)
    {
        if (!tool.Writes)
            return null;
        if (caller.ReadOnly)
            return ReadOnlyKeyMessage;
        if (!portfolio.CanEdit)
            return ViewOnlyFilter.Message;
        return null;
    }

    /// <summary>Whether the key may change this portfolio at all (shown in list_portfolios).</summary>
    public static bool CanEdit(ApiCaller caller, PortfolioSummary portfolio) => !caller.ReadOnly && portfolio.CanEdit;

    private async Task LogAsync(AgentTool tool, ApiCaller caller, int? portfolioId, JsonElement args, bool ok, string result)
    {
        // Reads aren't logged; a change (or an attempt at one) always is
        if (!tool.Writes || portfolioId is not { } id)
            return;
        try
        {
            await audit.LogAsync(caller, id, tool.Name, args.ValueKind == JsonValueKind.Undefined ? null : args.GetRawText(), ok, result);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Couldn't write the agent change log for {Tool}, key {KeyId}, portfolio {PortfolioId}", tool.Name, caller.ApiKeyId, id);
        }
    }

    private static AgentResult Fail(int status, string message) => new(status, null, new ApiError(message));
}
