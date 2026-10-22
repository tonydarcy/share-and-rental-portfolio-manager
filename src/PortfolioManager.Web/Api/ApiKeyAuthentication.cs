using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using PortfolioManager.Core.Api;

namespace PortfolioManager.Web.Api;

/// <summary>
/// Signs API and MCP requests in with a per-login API key, sent as "Authorization: Bearer pm_..."
/// or "X-Api-Key: pm_...". Only the API endpoints use it: the sign-in cookie never works there,
/// so another web site can't make changes through the API in a signed-in person's browser.
/// </summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, ApiKeyService keys)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";
    public const string KeyIdClaim = "pm_api_key_id";
    public const string KeyNameClaim = "pm_api_key_name";
    public const string ReadOnlyClaim = "pm_api_read_only";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var key = ReadKey(Request.Headers.Authorization.ToString(), Request.Headers["X-Api-Key"].ToString());
        if (key is null)
            return AuthenticateResult.NoResult();

        var caller = await keys.FindAsync(key);
        if (caller is null)
            return AuthenticateResult.Fail("That API key isn't right, or it has been revoked.");

        var identity = new ClaimsIdentity(Claims(caller), SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer";
        await Response.WriteAsJsonAsync(new ApiError(
            "Send an API key as \"Authorization: Bearer pm_...\". Make one on the API keys page (your name, top right, then API keys)."));
    }

    protected override async Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        await Response.WriteAsJsonAsync(new ApiError("This API key can't do that."));
    }

    /// <summary>The key from a Bearer Authorization header, or else the X-Api-Key header.</summary>
    public static string? ReadKey(string? authorization, string? apiKeyHeader)
    {
        const string bearer = "Bearer ";
        if (!string.IsNullOrWhiteSpace(authorization) && authorization.StartsWith(bearer, StringComparison.OrdinalIgnoreCase))
            return authorization[bearer.Length..].Trim() is { Length: > 0 } k ? k : null;
        return string.IsNullOrWhiteSpace(apiKeyHeader) ? null : apiKeyHeader.Trim();
    }

    public static IEnumerable<Claim> Claims(ApiCaller caller) =>
    [
        new(ClaimTypes.NameIdentifier, caller.UserId.ToString()),
        new(ClaimTypes.Name, caller.Email),
        new(KeyIdClaim, caller.ApiKeyId.ToString()),
        new(KeyNameClaim, caller.KeyName),
        new(ReadOnlyClaim, caller.ReadOnly ? "true" : "false"),
    ];

    /// <summary>The key's details back from a signed-in API request.</summary>
    public static ApiCaller Caller(ClaimsPrincipal user) => new(
        int.Parse(user.FindFirstValue(KeyIdClaim) ?? throw new InvalidOperationException("Not signed in with an API key")),
        int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!),
        user.FindFirstValue(KeyNameClaim) ?? "",
        user.FindFirstValue(ReadOnlyClaim) == "true",
        user.FindFirstValue(ClaimTypes.Name) ?? "");
}

/// <summary>The body of an API error. Fields lists what was wrong with each input, when known.</summary>
public sealed record ApiError(string Error, IReadOnlyDictionary<string, string>? Fields = null);
