using System.Data;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using PortfolioManager.Core.Data;

namespace PortfolioManager.Core.Api;

/// <summary>A login's API key as listed on the API keys page. The key itself is never stored.</summary>
public sealed record ApiKeyInfo(int ApiKeyId, string Name, string Prefix, bool ReadOnly, DateTime Created, DateTime? LastUsed, DateTime? Revoked)
{
    public bool Active => Revoked is null;
}

/// <summary>The login a working key acts as. ReadOnly keys can look but never change anything.</summary>
public sealed record ApiCaller(int ApiKeyId, int UserId, string KeyName, bool ReadOnly, string Email);

/// <summary>A key just made: Key is shown once and can't be seen again.</summary>
public sealed record NewApiKey(ProcResult Result, string? Key);

/// <summary>
/// Per-login API keys for the JSON API and the MCP server. A key is "pm_" and 43 random characters;
/// only its SHA-256 hash is kept, so a copy of the database can't be used to sign in.
/// </summary>
public sealed class ApiKeyService(IDbConnectionFactory db)
{
    public const string KeyStart = "pm_";
    public const int MaxNameLength = 100;
    private const int PrefixLength = 10;

    /// <summary>A new random key: "pm_" and 32 random bytes in URL-safe base64.</summary>
    public static string Generate() =>
        KeyStart + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>The SHA-256 of the key, as 64 lower-case hex characters.</summary>
    public static string Hash(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key.Trim())));

    /// <summary>The start of the key shown on the page, so keys can be told apart.</summary>
    public static string Prefix(string key) => key.Length <= PrefixLength ? key : key[..PrefixLength];

    /// <summary>Whether the text could be one of our keys, before going to the database.</summary>
    public static bool LooksLikeKey(string? key) =>
        key is { Length: > 20 and < 200 } && key.StartsWith(KeyStart, StringComparison.Ordinal);

    public static string? ValidateName(string? name)
    {
        var clean = name?.Trim();
        if (string.IsNullOrEmpty(clean))
            return "Give the key a name, such as the AI assistant or app that will use it.";
        if (clean.Length > MaxNameLength)
            return $"Keep the name to {MaxNameLength} characters.";
        return null;
    }

    public async Task<NewApiKey> CreateAsync(int userId, string name, bool readOnly)
    {
        if (ValidateName(name) is { } error)
            return new NewApiKey(ProcResult.Failed(error), null);

        var key = Generate();
        await using var con = db.Create();
        var row = await con.QuerySingleOrDefaultAsync<(string Result, int? Id)>("uspAddApiKey", new
        {
            iUserId = userId,
            strName = name.Trim(),
            strKeyPrefix = Prefix(key),
            strKeyHash = Hash(key),
            bReadOnly = readOnly,
        }, commandType: CommandType.StoredProcedure);
        var result = ProcResult.From(row.Result, "Key Added", row.Id);
        return new NewApiKey(result, result.Ok ? key : null);
    }

    public async Task<IReadOnlyList<ApiKeyInfo>> GetKeysAsync(int userId)
    {
        await using var con = db.Create();
        var rows = await con.QueryAsync("uspGetApiKeys", new { iUserId = userId }, commandType: CommandType.StoredProcedure);
        return rows.Cast<IDictionary<string, object?>>().Select(MapKey).ToList();
    }

    public static ApiKeyInfo MapKey(IDictionary<string, object?> r) => new(
        Convert.ToInt32(r["iApiKeyID"]),
        r["strName"]?.ToString()?.Trim() ?? "",
        r["strKeyPrefix"]?.ToString()?.Trim() ?? "",
        Convert.ToBoolean(r["bReadOnly"]),
        Convert.ToDateTime(r["dtmCreatedDate"]),
        r["dtmLastUsedDate"] as DateTime?,
        r["dtmRevokedDate"] as DateTime?);

    public async Task<ProcResult> RevokeAsync(int userId, int apiKeyId)
    {
        await using var con = db.Create();
        var result = await con.ExecuteScalarAsync<string>("uspRevokeApiKey",
            new { iUserId = userId, iApiKeyID = apiKeyId }, commandType: CommandType.StoredProcedure);
        return ProcResult.From(result, "Key Revoked");
    }

    /// <summary>The login a working key belongs to, or null for a wrong or revoked key.</summary>
    public async Task<ApiCaller?> FindAsync(string? key)
    {
        if (!LooksLikeKey(key))
            return null;
        await using var con = db.Create();
        var row = (IDictionary<string, object?>?)await con.QuerySingleOrDefaultAsync("uspFindApiKey",
            new { strKeyHash = Hash(key!) }, commandType: CommandType.StoredProcedure);
        return row is null ? null : MapCaller(row);
    }

    public static ApiCaller MapCaller(IDictionary<string, object?> r) => new(
        Convert.ToInt32(r["iApiKeyID"]),
        Convert.ToInt32(r["iUserID"]),
        r["strName"]?.ToString()?.Trim() ?? "",
        Convert.ToBoolean(r["bReadOnly"]),
        r["strLoginEmail"]?.ToString()?.Trim() ?? "");
}
