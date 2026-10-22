using System.Data;
using Dapper;
using PortfolioManager.Core.Data;

namespace PortfolioManager.Core.Api;

/// <summary>One change an API key or AI agent made, or tried to make.</summary>
public sealed record ApiAuditEntry(
    int AuditId,
    DateTime When,
    int PortfolioId,
    string PortfolioName,
    string KeyName,
    string KeyOwner,
    string Action,
    string Detail,
    bool Succeeded,
    string Result);

/// <summary>The agent change log (tblApiAuditLog): every write through the API or MCP server is recorded here.</summary>
public sealed class ApiAuditService(IDbConnectionFactory db)
{
    public const int MaxDetail = 4000;
    public const int MaxResult = 1000;

    public async Task LogAsync(ApiCaller caller, int portfolioId, string action, string? detail, bool succeeded, string? result)
    {
        await using var con = db.Create();
        await con.ExecuteAsync("uspAddApiAuditLog", Parameters(caller, portfolioId, action, detail, succeeded, result),
            commandType: CommandType.StoredProcedure);
    }

    public static object Parameters(ApiCaller caller, int portfolioId, string action, string? detail, bool succeeded, string? result) => new
    {
        iApiKeyID = caller.ApiKeyId,
        iUserId = caller.UserId,
        iPortfolioId = portfolioId,
        strAction = action,
        strDetail = Cut(detail, MaxDetail),
        bSucceeded = succeeded,
        strResult = Cut(result, MaxResult),
    };

    /// <summary>Recent agent changes the login may see: its own keys', and anyone's in portfolios it owns.</summary>
    public async Task<IReadOnlyList<ApiAuditEntry>> GetRecentAsync(int userId, int top = 200)
    {
        await using var con = db.Create();
        var rows = await con.QueryAsync("uspGetApiAuditLog", new { iUserId = userId, iTop = top }, commandType: CommandType.StoredProcedure);
        return rows.Cast<IDictionary<string, object?>>().Select(Map).ToList();
    }

    public static ApiAuditEntry Map(IDictionary<string, object?> r) => new(
        Convert.ToInt32(r["iApiAuditID"]),
        Convert.ToDateTime(r["dtmCreatedDate"]),
        Convert.ToInt32(r["iPortfolioID"]),
        r["PortfolioName"]?.ToString()?.Trim() ?? "",
        r["KeyName"]?.ToString()?.Trim() ?? "",
        r["KeyOwner"]?.ToString()?.Trim() ?? "",
        r["strAction"]?.ToString()?.Trim() ?? "",
        r["strDetail"]?.ToString() ?? "",
        Convert.ToBoolean(r["bSucceeded"]),
        r["strResult"]?.ToString() ?? "");

    public static string? Cut(string? text, int max) => text is null || text.Length <= max ? text : text[..(max - 1)] + "…";
}
