using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PortfolioManager.Mobile.Client;

/// <summary>The site the app talks to, as a clean address with no trailing slash.</summary>
public static class ServerAddress
{
    public const string Default = "https://app.shareportfoliomanager.com";

    /// <summary>
    /// The address the person typed, or null when it isn't one. https only, except http to this computer or the
    /// Android emulator's view of it (10.0.2.2), for testing against a copy of the site run on a PC.
    /// </summary>
    public static string? Normalise(string? typed)
    {
        var text = (typed ?? "").Trim().TrimEnd('/');
        if (text.Length == 0)
            return null;
        if (!text.Contains("://", StringComparison.Ordinal))
            text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var u) || u.Query.Length > 0 || u.Fragment.Length > 0)
            return null;
        var local = u.IsLoopback || u.Host == "10.0.2.2";
        if (u.Scheme != Uri.UriSchemeHttps && !(u.Scheme == Uri.UriSchemeHttp && local))
            return null;
        return u.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }
}

/// <summary>The PKCE pair for one sign-in (RFC 7636): the secret verifier and its S256 challenge.</summary>
public sealed record Pkce(string Verifier, string Challenge)
{
    public static Pkce Create() => FromVerifier(Base64Url(RandomNumberGenerator.GetBytes(32)));

    public static Pkce FromVerifier(string verifier) =>
        new(verifier, Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))));

    public static string NewState() => Base64Url(RandomNumberGenerator.GetBytes(16));

    internal static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>What came back to the app after sign-in: a code to swap, or why there isn't one.</summary>
public sealed record SignInCallback(string? Code, string? Error);

/// <summary>
/// Signs the app in to the site with OAuth: it registers itself once (the site hands back a client id), opens the
/// site's Connect page in the phone's browser, and swaps the code that comes back for an API key. The key is then
/// sent as a Bearer token on every API call; the person can see and revoke it on the site's API keys page.
/// </summary>
public sealed class SignInClient(HttpClient http)
{
    /// <summary>The address the phone hands back to the app after sign-in (the site allows this scheme).</summary>
    public const string RedirectScheme = "com.shareportfoliomanager.app";
    public const string RedirectUri = RedirectScheme + ":/oauth";
    public const string AppName = "Share Portfolio Manager phone app";

    /// <summary>Registers the app with the site and returns its client id. The id can be kept and reused.</summary>
    public async Task<string> RegisterAsync(string server, CancellationToken ct = default)
    {
        using var reply = await http.PostAsJsonAsync(server + "/oauth/register",
            new { client_name = AppName, redirect_uris = new[] { RedirectUri } }, ct);
        var body = await ReadAsync(reply, ct);
        return body.TryGetProperty("client_id", out var id) && id.GetString() is { Length: > 0 } s
            ? s
            : throw new SiteException("The site didn't give the app an id. Check the site address.");
    }

    /// <summary>The site's sign-in page for this app, to open in the phone's browser.</summary>
    public static Uri AuthorizeUrl(string server, string clientId, Pkce pkce, string state) =>
        new(server + "/Account/Connect?" + string.Join("&", new[]
        {
            ("response_type", "code"), ("client_id", clientId), ("redirect_uri", RedirectUri),
            ("code_challenge", pkce.Challenge), ("code_challenge_method", "S256"), ("state", state),
        }.Select(p => p.Item1 + "=" + Uri.EscapeDataString(p.Item2))));

    /// <summary>Reads the values the site sent back. A wrong state means the reply wasn't for this sign-in.</summary>
    public static SignInCallback ReadCallback(IReadOnlyDictionary<string, string> values, string expectedState)
    {
        string? Get(string key) => values.TryGetValue(key, out var v) && v.Length > 0 ? v : null;
        if (Get("state") != expectedState)
            return new(null, "The sign-in reply didn't match. Please try again.");
        if (Get("error") is { } error)
            return new(null, error == "access_denied" ? "Sign-in was cancelled." : Get("error_description") ?? error);
        return Get("code") is { } code ? new(code, null) : new(null, "The site didn't send a sign-in code.");
    }

    /// <summary>Swaps the one-time code for the app's key.</summary>
    public async Task<string> SwapCodeAsync(string server, string clientId, string code, Pkce pkce, CancellationToken ct = default)
    {
        using var reply = await http.PostAsync(server + "/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = clientId,
            ["code"] = code,
            ["redirect_uri"] = RedirectUri,
            ["code_verifier"] = pkce.Verifier,
        }), ct);
        var body = await ReadAsync(reply, ct);
        return body.TryGetProperty("access_token", out var t) && t.GetString() is { Length: > 0 } s
            ? s
            : throw new SiteException("The site didn't send a key.");
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage reply, CancellationToken ct)
    {
        JsonElement body;
        try
        {
            body = await reply.Content.ReadFromJsonAsync<JsonElement>(ct);
        }
        catch (JsonException)
        {
            throw new SiteException($"The site sent an unexpected reply ({(int)reply.StatusCode}). Check the site address.");
        }
        if (!reply.IsSuccessStatusCode)
            throw new SiteException(body.ValueKind == JsonValueKind.Object && body.TryGetProperty("error_description", out var d)
                ? d.GetString() ?? "Sign-in failed."
                : $"Sign-in failed ({(int)reply.StatusCode}).");
        return body;
    }
}

/// <summary>A problem worth showing the person, in plain words.</summary>
public sealed class SiteException(string message, bool signedOut = false) : Exception(message)
{
    /// <summary>The key was refused (revoked or deleted on the site): the app should sign in again.</summary>
    public bool SignedOut { get; } = signedOut;
}
