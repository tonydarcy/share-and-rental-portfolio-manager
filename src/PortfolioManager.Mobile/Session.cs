using PortfolioManager.Mobile.Client;

namespace PortfolioManager.Mobile;

/// <summary>
/// Who the app is signed in as. The key is kept in the phone's secure storage (Android Keystore, iOS Keychain);
/// the site address, the app's client id and the last portfolio picked are ordinary preferences.
/// </summary>
public sealed class Session(HttpClient http)
{
    private const string KeyName = "api_key";
    private string? key;

    public string Server
    {
        get => Preferences.Default.Get("server", ServerAddress.Default);
        set => Preferences.Default.Set("server", value);
    }

    public int LastPortfolioId
    {
        get => Preferences.Default.Get("portfolio", 0);
        set => Preferences.Default.Set("portfolio", value);
    }

    public bool IsSignedIn => Preferences.Default.Get("signed_in", false);

    public SiteApi Api => new(http, Server, key ?? throw new SiteException("Please sign in.", signedOut: true));

    /// <summary>Reads the key back from secure storage once the app starts.</summary>
    public async Task<bool> LoadAsync()
    {
        key ??= await SecureStorage.Default.GetAsync(KeyName);
        return key is not null;
    }

    /// <summary>Signs in through the site's own sign-in page in the phone's browser (passwords never touch the app).</summary>
    public async Task SignInAsync(string server)
    {
        var signIn = new SignInClient(http);
        var clientIdName = "client_id:" + server;
        var clientId = Preferences.Default.Get<string?>(clientIdName, null);
        if (clientId is null)
        {
            clientId = await signIn.RegisterAsync(server);
            Preferences.Default.Set(clientIdName, clientId);
        }

        var pkce = Pkce.Create();
        var state = Pkce.NewState();
        WebAuthenticatorResult result;
        try
        {
            result = await WebAuthenticator.Default.AuthenticateAsync(new WebAuthenticatorOptions
            {
                Url = SignInClient.AuthorizeUrl(server, clientId, pkce, state),
                CallbackUrl = new Uri(SignInClient.RedirectUri),
            });
        }
        catch (TaskCanceledException)
        {
            throw new SiteException("Sign-in was cancelled.");
        }

        var callback = SignInClient.ReadCallback(result.Properties, state);
        if (callback.Code is null)
        {
            // A client id from an older copy of the site (its keys changed) is refused: register again next time
            if (callback.Error?.Contains("client", StringComparison.OrdinalIgnoreCase) == true)
                Preferences.Default.Remove(clientIdName);
            throw new SiteException(callback.Error ?? "Sign-in failed.");
        }

        var newKey = await signIn.SwapCodeAsync(server, clientId, callback.Code, pkce);
        await SecureStorage.Default.SetAsync(KeyName, newKey);
        key = newKey;
        Server = server;
        Preferences.Default.Set("signed_in", true);
    }

    /// <summary>Forgets the key on this phone. It stays on the site's API keys page until deleted there.</summary>
    public void SignOut()
    {
        SecureStorage.Default.Remove(KeyName);
        key = null;
        Preferences.Default.Set("signed_in", false);
        LastPortfolioId = 0;
    }
}
