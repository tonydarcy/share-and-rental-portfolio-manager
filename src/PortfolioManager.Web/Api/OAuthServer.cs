using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Memory;
using PortfolioManager.Core.Api;

namespace PortfolioManager.Web.Api;

/// <summary>An app that registered itself to sign in (RFC 7591), such as Claude.</summary>
public sealed record OAuthClient(string Name, IReadOnlyList<string> RedirectUris);

/// <summary>A checked request to sign an app in, shown on the Connect page for the person to allow.</summary>
public sealed record OAuthRequest(string ClientId, OAuthClient Client, string RedirectUri, string CodeChallenge, string? State);

/// <summary>What the person allowed: the login, and whether the app may only look.</summary>
public sealed record OAuthGrant(int UserId, bool ReadOnly, string ClientName);

/// <summary>Why a sign-in request was refused. Redirect is false when the app's address can't be trusted with the answer.</summary>
public sealed record OAuthProblem(string Error, string Description, bool Redirect);

/// <summary>
/// OAuth 2.1 sign-in for the MCP server and the JSON API, so an assistant such as Claude can connect by sending the
/// person to this site to log in and allow it, instead of them pasting a key. Apps register themselves (dynamic client
/// registration), the person allows them on the Connect page, and the app swaps the one-time code (checked with PKCE)
/// for an API key, which then shows on the API keys page like any other and is revoked there.
/// Nothing is stored for apps or codes: both are signed with the Data Protection keys, as the account email links are.
/// </summary>
public sealed class OAuthServer(IDataProtectionProvider provider, IMemoryCache memory)
{
    public const string ClientIdStart = "pmc_";
    public const string AuthorizePath = "/Account/Connect";
    public const string TokenPath = "/oauth/token";
    public const string RegisterPath = "/oauth/register";
    public const string ServerMetadataPath = "/.well-known/oauth-authorization-server";
    public const string ResourceMetadataPath = "/.well-known/oauth-protected-resource";
    public static readonly TimeSpan CodeLife = TimeSpan.FromMinutes(5);
    private const int MaxClientName = 60;
    private const int MaxRedirectUris = 10;

    private readonly IDataProtector clients = provider.CreateProtector("PortfolioManager.OAuthClient");
    private readonly Lock usedLock = new();
    private readonly ITimeLimitedDataProtector codes = provider.CreateProtector("PortfolioManager.OAuthCode").ToTimeLimitedDataProtector();

    /// <summary>This site's address as the app reached it, which OAuth needs in every link it hands out.</summary>
    public static string BaseUrl(HttpRequest request) => $"{request.Scheme}://{request.Host}{request.PathBase}";

    /// <summary>Tells an app hitting /mcp where to sign in (RFC 9728).</summary>
    public static JsonObject ResourceMetadata(string baseUrl, string name) => new()
    {
        ["resource"] = baseUrl + "/mcp",
        ["authorization_servers"] = new JsonArray(baseUrl),
        ["bearer_methods_supported"] = new JsonArray("header"),
        ["resource_name"] = name,
    };

    /// <summary>The sign-in addresses and what they support (RFC 8414).</summary>
    public static JsonObject ServerMetadata(string baseUrl) => new()
    {
        ["issuer"] = baseUrl,
        ["authorization_endpoint"] = baseUrl + AuthorizePath,
        ["token_endpoint"] = baseUrl + TokenPath,
        ["registration_endpoint"] = baseUrl + RegisterPath,
        ["response_types_supported"] = new JsonArray("code"),
        ["grant_types_supported"] = new JsonArray("authorization_code"),
        ["code_challenge_methods_supported"] = new JsonArray("S256"),
        ["token_endpoint_auth_methods_supported"] = new JsonArray("none"),
    };

    /// <summary>The WWW-Authenticate header on a 401 from /mcp or the API, pointing at the metadata above.</summary>
    public static string Challenge(string baseUrl) => $"Bearer resource_metadata=\"{baseUrl}{ResourceMetadataPath}/mcp\"";

    /// <summary>
    /// Registers an app. Its name and return addresses are signed into the client id, so there is no table to fill up.
    /// Apps get no secret: PKCE protects the code instead, as OAuth 2.1 expects for apps that can't keep one.
    /// </summary>
    public (JsonObject? Client, OAuthProblem? Problem) Register(JsonObject? body)
    {
        if (body?["redirect_uris"] is not JsonArray uris || uris.Count is 0 or > MaxRedirectUris)
            return (null, new OAuthProblem("invalid_redirect_uri", "Send redirect_uris: the addresses to return to after sign-in.", false));
        var list = new List<string>();
        foreach (var u in uris)
        {
            var uri = u is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
            if (uri is null || !AllowedRedirect(uri))
                return (null, new OAuthProblem("invalid_redirect_uri",
                    "Each redirect_uri must be an https address, or http on this computer (localhost).", false));
            list.Add(uri);
        }

        var name = body["client_name"] is JsonValue n && n.TryGetValue<string>(out var cn) && cn.Trim().Length > 0 ? cn.Trim() : "Connected app";
        if (name.Length > MaxClientName)
            name = name[..MaxClientName].Trim();

        var client = new OAuthClient(name, list);
        var id = ClientIdStart + clients.Protect(JsonSerializer.Serialize(client));
        return (new JsonObject
        {
            ["client_id"] = id,
            ["client_id_issued_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["client_name"] = name,
            ["redirect_uris"] = new JsonArray(list.Select(x => (JsonNode)x).ToArray()),
            ["grant_types"] = new JsonArray("authorization_code"),
            ["response_types"] = new JsonArray("code"),
            ["token_endpoint_auth_method"] = "none",
        }, null);
    }

    /// <summary>The app a client id was given to, or null when it is forged or wasn't made here.</summary>
    public OAuthClient? FindClient(string? clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId) || !clientId.StartsWith(ClientIdStart, StringComparison.Ordinal))
            return null;
        try
        {
            return JsonSerializer.Deserialize<OAuthClient>(clients.Unprotect(clientId[ClientIdStart.Length..]));
        }
        catch (Exception e) when (e is CryptographicException or FormatException or JsonException)
        {
            return null;
        }
    }

    /// <summary>An https address, or http back to this computer for desktop apps (RFC 8252). Never a fragment.</summary>
    public static bool AllowedRedirect(string? uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var u)
        && string.IsNullOrEmpty(u.Fragment)
        && (u.Scheme == Uri.UriSchemeHttps || (u.Scheme == Uri.UriSchemeHttp && u.IsLoopback));

    /// <summary>Checks the sign-in request an app sent the person with (the authorization endpoint's query).</summary>
    public (OAuthRequest? Request, OAuthProblem? Problem) Check(Func<string, string?> value)
    {
        var clientId = value("client_id");
        var client = FindClient(clientId);
        if (client is null)
            return (null, new OAuthProblem("invalid_client", "This app isn't registered with this site. Go back to the app and connect again.", false));

        // With one return address the app may leave it out; otherwise it must name one it registered
        var redirect = value("redirect_uri") is { Length: > 0 } r ? r : client.RedirectUris.Count == 1 ? client.RedirectUris[0] : null;
        if (redirect is null || !client.RedirectUris.Contains(redirect, StringComparer.Ordinal))
            return (null, new OAuthProblem("invalid_request", "The app's return address doesn't match the one it registered.", false));

        var request = new OAuthRequest(clientId!, client, redirect, value("code_challenge") ?? "", value("state"));
        if (value("response_type") != "code")
            return (request, new OAuthProblem("unsupported_response_type", "Only response_type=code is supported.", true));
        if (value("code_challenge_method") != "S256" || request.CodeChallenge.Length is < 43 or > 128)
            return (request, new OAuthProblem("invalid_request", "PKCE is required: send code_challenge with code_challenge_method=S256.", true));
        return (request, null);
    }

    /// <summary>The one-time code sent back to the app once the person allows it. It works for five minutes.</summary>
    public string IssueCode(OAuthRequest request, int userId, bool readOnly)
    {
        var code = new CodeData(Guid.NewGuid().ToString("N"), Fingerprint(request.ClientId), request.RedirectUri,
            request.CodeChallenge, userId, readOnly, request.Client.Name);
        return codes.Protect(JsonSerializer.Serialize(code), CodeLife);
    }

    /// <summary>
    /// Swaps a code for what the person allowed, or null when the code is forged, expired, already used, for another
    /// app or return address, or the PKCE verifier doesn't match.
    /// </summary>
    public OAuthGrant? Redeem(string? code, string? clientId, string? redirectUri, string? verifier)
    {
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrEmpty(clientId) || verifier is not { Length: >= 43 and <= 128 })
            return null;
        CodeData? data;
        try
        {
            data = JsonSerializer.Deserialize<CodeData>(codes.Unprotect(code.Trim()));
        }
        catch (Exception e) when (e is CryptographicException or FormatException or JsonException)
        {
            return null;
        }
        if (data is null || data.Client != Fingerprint(clientId) || (redirectUri is { Length: > 0 } && redirectUri != data.Redirect)
            || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(S256(verifier)), Encoding.ASCII.GetBytes(data.Challenge)))
            return null;

        // Each code works once. Kept in memory past its life, so a copy can't be swapped again (one server, as on IIS)
        lock (usedLock)
        {
            if (memory.TryGetValue(UsedKey(data.Id), out _))
                return null;
            memory.Set(UsedKey(data.Id), true, CodeLife * 2);
        }
        return new OAuthGrant(data.UserId, data.ReadOnly, data.Name);
    }

    /// <summary>The name of the API key made for a connected app, as listed on the API keys page.</summary>
    public static string KeyName(string clientName) => $"{clientName} (connected {DateTime.Today:yyyy-MM-dd})";

    /// <summary>The app's return address with the answer added to its query.</summary>
    public static string RedirectWith(string redirectUri, params (string Key, string? Value)[] values) =>
        QueryHelpers.AddQueryString(redirectUri, values.Where(v => v.Value is not null).Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)));

    /// <summary>The PKCE S256 challenge for a verifier: base64url of its SHA-256.</summary>
    public static string S256(string verifier) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Fingerprint(string clientId) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(clientId)))[..32];

    private static string UsedKey(string id) => "oauth-code:" + id;

    private sealed record CodeData(string Id, string Client, string Redirect, string Challenge, int UserId, bool ReadOnly, string Name);
}

/// <summary>The OAuth addresses: discovery, app registration and the token swap. The Connect page is the sign-in step.</summary>
public static class OAuthEndpoints
{
    public static void MapOAuth(this WebApplication app)
    {
        // Discovery, at the root and with the resource's path added, as MCP clients look in both places
        app.MapGet(OAuthServer.ResourceMetadataPath, (HttpContext http, Services.SiteBranding site) =>
            Results.Json(OAuthServer.ResourceMetadata(OAuthServer.BaseUrl(http.Request), site.Name))).AllowAnonymous();
        app.MapGet(OAuthServer.ResourceMetadataPath + "/mcp", (HttpContext http, Services.SiteBranding site) =>
            Results.Json(OAuthServer.ResourceMetadata(OAuthServer.BaseUrl(http.Request), site.Name))).AllowAnonymous();
        app.MapGet(OAuthServer.ServerMetadataPath, (HttpContext http) =>
            Results.Json(OAuthServer.ServerMetadata(OAuthServer.BaseUrl(http.Request)))).AllowAnonymous();

        var oauth = app.MapGroup("/oauth").AllowAnonymous().RequireRateLimiting(ApiEndpoints.RateLimit).DisableAntiforgery();

        oauth.MapPost("/register", async Task<IResult> (HttpContext http, OAuthServer server) =>
        {
            JsonObject? body;
            try
            {
                body = await JsonNode.ParseAsync(http.Request.Body) as JsonObject;
            }
            catch (JsonException)
            {
                body = null;
            }
            if (body is null)
                return Problem("invalid_client_metadata", "Send the app's details as a JSON object.");
            var (client, problem) = server.Register(body);
            return problem is not null ? Problem(problem.Error, problem.Description) : Results.Json(client, statusCode: StatusCodes.Status201Created);
        });

        oauth.MapPost("/token", async Task<IResult> (HttpContext http, OAuthServer server, ApiKeyService keys) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (!http.Request.HasFormContentType)
                return Problem("invalid_request", "Send the token request as a form (application/x-www-form-urlencoded).");
            var form = await http.Request.ReadFormAsync();
            if (form["grant_type"] != "authorization_code")
                return Problem("unsupported_grant_type", "Only grant_type=authorization_code is supported.");

            var clientId = form["client_id"].ToString() is { Length: > 0 } id ? id : BasicClientId(http.Request.Headers.Authorization);
            var grant = server.Redeem(form["code"], clientId, form["redirect_uri"], form["code_verifier"]);
            if (grant is null)
                return Problem("invalid_grant", "That sign-in code isn't right, has expired or was already used. Connect again.");

            // The token is an ordinary API key, so the API keys page lists it, logs what it changes and can revoke it
            var made = await keys.CreateAsync(grant.UserId, OAuthServer.KeyName(grant.ClientName), grant.ReadOnly);
            if (!made.Result.Ok)
                return Problem("server_error", made.Result.Error ?? "The key couldn't be made.", StatusCodes.Status500InternalServerError);
            return Results.Json(new JsonObject { ["access_token"] = made.Key, ["token_type"] = "Bearer" });
        });
    }

    /// <summary>The client id from an HTTP Basic header, for apps that send it that way.</summary>
    public static string? BasicClientId(string? authorization)
    {
        const string basic = "Basic ";
        if (string.IsNullOrEmpty(authorization) || !authorization.StartsWith(basic, StringComparison.OrdinalIgnoreCase))
            return null;
        try
        {
            var pair = Encoding.UTF8.GetString(Convert.FromBase64String(authorization[basic.Length..].Trim()));
            return Uri.UnescapeDataString(pair.Split(':', 2)[0]);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static IResult Problem(string error, string description, int status = StatusCodes.Status400BadRequest) =>
        Results.Json(new JsonObject { ["error"] = error, ["error_description"] = description }, statusCode: status);
}
