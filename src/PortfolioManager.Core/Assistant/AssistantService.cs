using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using PortfolioManager.Core.Data;

namespace PortfolioManager.Core.Assistant;

/// <summary>A login's assistant settings. The key is stored encrypted; KeyHint is its last few characters.</summary>
public sealed record AssistantSettings(string? ApiKeyProtected, string? KeyHint, string? Model)
{
    public static readonly AssistantSettings None = new(null, null, null);
}

/// <summary>The assistant's saved settings and conversations (2026-10-23-03-assistant.sql).</summary>
public sealed class AssistantService(IDbConnectionFactory db)
{
    /// <summary>The login's settings; None before the migration has been run.</summary>
    public async Task<AssistantSettings> GetSettingsAsync(int loginId)
    {
        try
        {
            await using var con = db.Create();
            return await con.QuerySingleOrDefaultAsync<AssistantSettings>("uspGetAssistantSettings", new { iLoginID = loginId },
                commandType: CommandType.StoredProcedure) ?? AssistantSettings.None;
        }
        catch (SqlException ex) when (ex.Number == 2812)
        {
            return AssistantSettings.None;
        }
    }

    /// <summary>Saves the model, and the key when one is given (null leaves the saved key alone; clearKey removes it).</summary>
    public async Task<ProcResult> SaveSettingsAsync(int loginId, string? apiKeyProtected, string? keyHint, string model, bool clearKey = false)
    {
        await using var con = db.Create();
        var result = await con.QuerySingleOrDefaultAsync<string>("uspSaveAssistantSettings",
            new { iLoginID = loginId, strApiKeyProtected = apiKeyProtected, strKeyHint = keyHint, strModel = model, bClearKey = clearKey },
            commandType: CommandType.StoredProcedure);
        return ProcResult.From(result, "Success");
    }

    public async Task<AssistantTranscript> GetChatAsync(int loginId, int portfolioId)
    {
        try
        {
            await using var con = db.Create();
            return AssistantTranscript.Parse(await con.QuerySingleOrDefaultAsync<string>("uspGetAssistantChat",
                new { iLoginID = loginId, iPortfolioID = portfolioId }, commandType: CommandType.StoredProcedure));
        }
        catch (SqlException ex) when (ex.Number == 2812)
        {
            return AssistantTranscript.Empty();
        }
    }

    public async Task SaveChatAsync(int loginId, int portfolioId, AssistantTranscript transcript)
    {
        transcript.Trim();
        await using var con = db.Create();
        await con.ExecuteAsync("uspSaveAssistantChat", new { iLoginID = loginId, iPortfolioID = portfolioId, strTranscriptJson = transcript.Serialize() },
            commandType: CommandType.StoredProcedure);
    }

    public Task ClearChatAsync(int loginId, int portfolioId) => SaveChatAsync(loginId, portfolioId, AssistantTranscript.Empty());
}
