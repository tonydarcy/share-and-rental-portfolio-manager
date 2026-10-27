using System.Data;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Data;

namespace PortfolioManager.Core.Exports;

/// <summary>
/// The site's own app registrations with Xero and MYOB (appsettings "AccountingLink"). The host registers the site once
/// at developer.xero.com and developer.myob.com with the redirect address /Accounting/Connected, and every portfolio
/// then connects to its own organisation. Without a client id, the export page offers only the import files.
/// </summary>
public sealed class AccountingLinkOptions
{
    public AppRegistration Xero { get; set; } = new() { Scopes = DefaultXeroScopes };
    public AppRegistration Myob { get; set; } = new() { Scopes = DefaultMyobScopes, ApiBase = DefaultMyobApiBase };

    // Apps registered with Xero since 2 March 2026 must ask for the narrow scopes; manual journals and reading tax rates is all this needs.
    public const string DefaultXeroScopes = "offline_access accounting.manualjournals accounting.settings.read";
    public const string DefaultMyobScopes = "sme-general-ledger";
    public const string DefaultMyobApiBase = "https://api.myob.com/accountright/";

    public AppRegistration For(string system) => system == ExportSystems.Myob ? Myob : Xero;

    public bool IsConfigured(string system) => For(system) is { ClientId.Length: > 0, ClientSecret.Length: > 0 };
}

public sealed class AppRegistration
{
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string Scopes { get; set; } = "";

    /// <summary>MYOB only: the address company files are reached at, before the business id.</summary>
    public string ApiBase { get; set; } = "";
}

/// <summary>Encrypts the sign-in tokens before they are saved (the web site's Data Protection key ring).</summary>
public interface IAccountingTokenProtector
{
    string Protect(string tokens);
    string? Unprotect(string protectedTokens);
}

public sealed record AccountingTokens(string AccessToken, string RefreshToken, DateTime ExpiresUtc)
{
    public bool IsExpired(DateTime nowUtc) => nowUtc >= ExpiresUtc.AddMinutes(-2);
}

public sealed record AccountingConnection(string System, string OrganisationId, string? OrganisationName, DateTime Connected, string? ConnectedBy);

public sealed record AccountingSend(string System, string Shape, DateOnly? From, DateOnly To, int Journals, int Sent, string? Result, DateTime SentAt, string? SentBy)
{
    public bool Overlaps(DateOnly? from, DateOnly to) => (From ?? DateOnly.MinValue) <= to && (from ?? DateOnly.MinValue) <= To;
}

public sealed record SendOutcome(int Sent, IReadOnlyList<string> Errors)
{
    public bool Ok => Errors.Count == 0;
}

/// <summary>The addresses and request bodies for Xero's and MYOB's APIs, kept apart from the HTTP calls so they can be tested.</summary>
public static class AccountingApi
{
    public const string XeroAuthorize = "https://login.xero.com/identity/connect/authorize";
    public const string XeroToken = "https://identity.xero.com/connect/token";
    public const string XeroConnections = "https://api.xero.com/connections";
    public const string XeroApi = "https://api.xero.com/api.xro/2.0/";
    public const string MyobAuthorize = "https://secure.myob.com/oauth2/account/authorize";
    public const string MyobToken = "https://secure.myob.com/oauth2/v1/authorize";

    /// <summary>Xero takes up to this many manual journals in one request.</summary>
    public const int XeroBatch = 50;

    public static string AuthorizeUrl(string system, AppRegistration app, string redirectUri, string state)
    {
        var q = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = app.ClientId,
            ["redirect_uri"] = redirectUri,
            ["scope"] = app.Scopes,
            ["state"] = state,
        };
        if (system == ExportSystems.Myob)
            q["prompt"] = "consent";
        return (system == ExportSystems.Myob ? MyobAuthorize : XeroAuthorize) + "?" +
               string.Join("&", q.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));
    }

    /// <summary>The tokens from a token response, or null when it has none.</summary>
    public static AccountingTokens? ReadTokens(JsonElement body, DateTime nowUtc, string? previousRefresh = null)
    {
        if (!body.TryGetProperty("access_token", out var access) || access.GetString() is not { Length: > 0 } a)
            return null;
        var refresh = body.TryGetProperty("refresh_token", out var r) && r.GetString() is { Length: > 0 } rt ? rt : previousRefresh;
        var seconds = body.TryGetProperty("expires_in", out var e)
            ? e.ValueKind == JsonValueKind.Number ? e.GetInt32() : int.TryParse(e.GetString(), out var s) ? s : 1800
            : 1800;
        return refresh is null ? null : new AccountingTokens(a, refresh, nowUtc.AddSeconds(seconds));
    }

    public static string SaveTokens(AccountingTokens t) => JsonSerializer.Serialize(t);

    public static AccountingTokens? LoadTokens(string? json)
    {
        try
        {
            return json is { Length: > 0 } ? JsonSerializer.Deserialize<AccountingTokens>(json) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Xero's tax types by the name or code a mapping may hold ("BAS Excluded" and "BASEXCLUDED" both work), from
    /// a GET TaxRates response.
    /// </summary>
    public static IReadOnlyDictionary<string, string> XeroTaxTypes(JsonElement taxRates)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (taxRates.TryGetProperty("TaxRates", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var t in list.EnumerateArray())
                if (t.TryGetProperty("TaxType", out var type) && type.GetString() is { Length: > 0 } code)
                {
                    map[code] = code;
                    if (t.TryGetProperty("Name", out var name) && name.GetString() is { Length: > 0 } n)
                        map.TryAdd(n, code);
                }
        return map;
    }

    /// <summary>The manual journals request: drafts, so they are checked and posted in Xero, as with the import file.</summary>
    public static (string Json, IReadOnlyList<string> Missing) XeroManualJournals(IEnumerable<ExportJournal> journals, IReadOnlyDictionary<string, string> taxTypes)
    {
        var missing = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var array = new JsonArray();
        foreach (var j in journals)
        {
            var lines = new JsonArray();
            foreach (var l in j.Lines)
            {
                if (!taxTypes.TryGetValue(l.Tax, out var taxType))
                    missing.Add(l.Tax);
                lines.Add(new JsonObject
                {
                    ["LineAmount"] = l.Amount,
                    ["AccountCode"] = l.Code,
                    ["Description"] = Cut(l.Description, 4000),
                    ["TaxType"] = taxType ?? l.Tax,
                });
            }
            array.Add(new JsonObject
            {
                ["Narration"] = Cut(j.Narration, 4000),
                ["Date"] = j.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["Status"] = "DRAFT",
                ["JournalLines"] = lines,
            });
        }
        return (new JsonObject { ["ManualJournals"] = array }.ToJsonString(), missing.ToList());
    }

    /// <summary>MYOB's ids for each account (by its display number, such as 1-1100) or tax code (such as N-T), from a list response.</summary>
    public static IReadOnlyDictionary<string, string> MyobUids(JsonElement list, string keyProperty)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (list.TryGetProperty("Items", out var items) && items.ValueKind == JsonValueKind.Array)
            foreach (var i in items.EnumerateArray())
                if (i.TryGetProperty(keyProperty, out var key) && key.GetString() is { Length: > 0 } k && i.TryGetProperty("UID", out var uid))
                    map.TryAdd(k.Trim(), uid.GetString() ?? "");
        return map;
    }

    /// <summary>One MYOB general journal: amounts are positive with IsCredit for the credit side, and tax is already its own line.</summary>
    public static (string Json, IReadOnlyList<string> Missing) MyobGeneralJournal(ExportJournal j, IReadOnlyDictionary<string, string> accounts,
        IReadOnlyDictionary<string, string> taxCodes)
    {
        var missing = new List<string>();
        var lines = new JsonArray();
        foreach (var l in j.Lines)
        {
            if (!accounts.TryGetValue(l.Code, out var account))
                missing.Add($"account {l.Code}");
            if (!taxCodes.TryGetValue(l.Tax, out var tax))
                missing.Add($"tax code {l.Tax}");
            lines.Add(new JsonObject
            {
                ["Account"] = new JsonObject { ["UID"] = account ?? "" },
                ["TaxCode"] = new JsonObject { ["UID"] = tax ?? "" },
                ["Amount"] = Math.Abs(l.Amount),
                ["IsCredit"] = l.Amount < 0,
                ["Memo"] = Cut(l.Description, 255),
            });
        }
        var body = new JsonObject
        {
            ["DateOccurred"] = j.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "T00:00:00",
            ["Memo"] = Cut(j.Narration, 255),
            ["IsTaxInclusive"] = false,
            ["GSTReportingMethod"] = "Sale",
            ["Lines"] = lines,
        };
        return (body.ToJsonString(), missing.Distinct().ToList());
    }

    /// <summary>The messages in an error answer from either system, in a few words.</summary>
    public static string ErrorText(string body, int status)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var messages = new List<string>();
            void Walk(JsonElement e)
            {
                switch (e.ValueKind)
                {
                    case JsonValueKind.Object:
                        foreach (var p in e.EnumerateObject())
                            if (p.Name is "Message" or "message" or "Detail" or "error_description" && p.Value.ValueKind == JsonValueKind.String)
                                messages.Add(p.Value.GetString()!);
                            else
                                Walk(p.Value);
                        break;
                    case JsonValueKind.Array:
                        foreach (var i in e.EnumerateArray())
                            Walk(i);
                        break;
                }
            }
            Walk(doc.RootElement);
            if (messages.Count > 0)
                return string.Join(" ", messages.Distinct().Take(5));
        }
        catch (JsonException)
        {
        }
        return status switch
        {
            401 or 403 => "The connection was refused. Connect again.",
            429 => "Too many requests at once. Wait a minute and send again.",
            _ => $"The other system answered with error {status}.",
        };
    }

    private static string Cut(string s, int max) => s.Length <= max ? s : s[..max];
}

/// <summary>The HTTP calls to Xero and MYOB.</summary>
public sealed class AccountingApiClient(HttpClient http, AccountingLinkOptions options)
{
    public async Task<AccountingTokens> ExchangeCodeAsync(string system, string code, string redirectUri, CancellationToken ct = default) =>
        await TokenAsync(system, new()
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
        }, null, ct);

    public async Task<AccountingTokens> RefreshAsync(string system, AccountingTokens tokens, CancellationToken ct = default) =>
        await TokenAsync(system, new()
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = tokens.RefreshToken,
        }, tokens.RefreshToken, ct);

    private async Task<AccountingTokens> TokenAsync(string system, Dictionary<string, string> form, string? previousRefresh, CancellationToken ct)
    {
        var app = options.For(system);
        using var request = new HttpRequestMessage(HttpMethod.Post, system == ExportSystems.Myob ? AccountingApi.MyobToken : AccountingApi.XeroToken);
        if (system == ExportSystems.Myob)
        {
            // MYOB takes the app's id and secret (and the scope) in the form
            form["client_id"] = app.ClientId;
            form["client_secret"] = app.ClientSecret;
            form["scope"] = app.Scopes;
        }
        else
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{app.ClientId}:{app.ClientSecret}")));
        request.Content = new FormUrlEncodedContent(form);
        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new AccountingLinkException(AccountingApi.ErrorText(body, (int)response.StatusCode));
        using var doc = JsonDocument.Parse(body);
        return AccountingApi.ReadTokens(doc.RootElement, DateTime.UtcNow, previousRefresh)
            ?? throw new AccountingLinkException("The sign-in didn't return a token. Connect again.");
    }

    /// <summary>The Xero organisation the sign-in chose (the first if several were ticked).</summary>
    public async Task<(string Id, string Name)> GetXeroTenantAsync(AccountingTokens tokens, CancellationToken ct = default)
    {
        using var doc = await GetAsync(ExportSystems.Xero, AccountingApi.XeroConnections, tokens, null, ct);
        foreach (var c in doc.RootElement.EnumerateArray())
            if (c.TryGetProperty("tenantId", out var id) && (!c.TryGetProperty("tenantType", out var type) || type.GetString() == "ORGANISATION"))
                return (id.GetString()!, c.TryGetProperty("tenantName", out var n) ? n.GetString() ?? "" : "");
        throw new AccountingLinkException("No Xero organisation was chosen. Connect again and tick the organisation.");
    }

    public async Task<SendOutcome> SendXeroAsync(AccountingTokens tokens, string tenantId, IReadOnlyList<ExportJournal> journals, CancellationToken ct = default)
    {
        IReadOnlyDictionary<string, string> taxTypes;
        using (var rates = await GetAsync(ExportSystems.Xero, AccountingApi.XeroApi + "TaxRates", tokens, tenantId, ct))
            taxTypes = AccountingApi.XeroTaxTypes(rates.RootElement);
        var sent = 0;
        var errors = new List<string>();
        foreach (var batch in journals.Chunk(AccountingApi.XeroBatch))
        {
            var (json, missing) = AccountingApi.XeroManualJournals(batch, taxTypes);
            if (missing.Count > 0)
                return new SendOutcome(sent, [$"Xero has no tax rate called {string.Join(", ", missing)}. Fix the tax column below and send again."]);
            using var request = new HttpRequestMessage(HttpMethod.Put, AccountingApi.XeroApi + "ManualJournals");
            Authorise(request, ExportSystems.Xero, tokens, tenantId);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                errors.Add(AccountingApi.ErrorText(await response.Content.ReadAsStringAsync(ct), (int)response.StatusCode));
                break;
            }
            sent += batch.Length;
        }
        return new SendOutcome(sent, errors);
    }

    public async Task<SendOutcome> SendMyobAsync(AccountingTokens tokens, string businessId, IReadOnlyList<ExportJournal> journals, CancellationToken ct = default)
    {
        var root = MyobRoot(businessId);
        IReadOnlyDictionary<string, string> accounts, taxCodes;
        using (var a = await GetAsync(ExportSystems.Myob, root + "GeneralLedger/Account?$top=1000", tokens, null, ct))
            accounts = AccountingApi.MyobUids(a.RootElement, "DisplayID");
        using (var t = await GetAsync(ExportSystems.Myob, root + "GeneralLedger/TaxCode?$top=1000", tokens, null, ct))
            taxCodes = AccountingApi.MyobUids(t.RootElement, "Code");
        var sent = 0;
        foreach (var j in journals)
        {
            var (json, missing) = AccountingApi.MyobGeneralJournal(j, accounts, taxCodes);
            if (missing.Count > 0)
                return new SendOutcome(sent, [$"MYOB has no {string.Join(", ", missing)}. Fix the codes below and send again; {sent} journal{(sent == 1 ? "" : "s")} went before this."]);
            using var request = new HttpRequestMessage(HttpMethod.Post, root + "GeneralLedger/GeneralJournal");
            Authorise(request, ExportSystems.Myob, tokens, null);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return new SendOutcome(sent, [$"{j.Narration}: {AccountingApi.ErrorText(await response.Content.ReadAsStringAsync(ct), (int)response.StatusCode)}"]);
            sent++;
        }
        return new SendOutcome(sent, []);
    }

    private string MyobRoot(string businessId)
    {
        var b = options.Myob.ApiBase is { Length: > 0 } s ? s : AccountingLinkOptions.DefaultMyobApiBase;
        return (b.EndsWith('/') ? b : b + "/") + Uri.EscapeDataString(businessId) + "/";
    }

    private void Authorise(HttpRequestMessage request, string system, AccountingTokens tokens, string? tenantId)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (tenantId is not null)
            request.Headers.Add("xero-tenant-id", tenantId);
        if (system == ExportSystems.Myob)
        {
            request.Headers.Add("x-myobapi-key", options.Myob.ClientId);
            request.Headers.Add("x-myobapi-version", "v2");
        }
    }

    private async Task<JsonDocument> GetAsync(string system, string url, AccountingTokens tokens, string? tenantId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        Authorise(request, system, tokens, tenantId);
        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new AccountingLinkException(AccountingApi.ErrorText(body, (int)response.StatusCode));
        return JsonDocument.Parse(body);
    }
}

public sealed class AccountingLinkException(string message) : Exception(message);

/// <summary>A portfolio's connections to Xero and MYOB and the sends made through them (2026-10-26-09-accounting-connections.sql).</summary>
public sealed class AccountingLinkService(IDbConnectionFactory db, AccountingApiClient api, IAccountingTokenProtector protector, AccountingLinkOptions options)
{
    public bool IsConfigured(string system) => options.IsConfigured(system);

    private sealed record Row(string System, string OrganisationId, string? OrganisationName, string Tokens, DateTime Connected, string? ConnectedBy);

    private async Task<Row?> GetRowAsync(int portfolioId, string system)
    {
        await using var con = db.Create();
        try
        {
            return await con.QuerySingleOrDefaultAsync<Row>("""
                SELECT C.strSystem AS System, C.strOrganisationID AS OrganisationId, C.strOrganisationName AS OrganisationName, C.strTokens AS Tokens,
                       C.dtmConnected AS Connected, U.strLoginEmail AS ConnectedBy
                FROM tblAccountingConnections C LEFT JOIN tblUsers U ON U.iUserId = C.iConnectedByUserID
                WHERE C.iUserID = @portfolioId AND C.strSystem = @system
                """, new { portfolioId, system });
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208)
        {
            return null;
        }
    }

    public async Task<AccountingConnection?> GetAsync(int portfolioId, string system) =>
        await GetRowAsync(portfolioId, system) is { } r ? new AccountingConnection(r.System, r.OrganisationId, r.OrganisationName, r.Connected, r.ConnectedBy) : null;

    public async Task<IReadOnlyList<AccountingSend>> GetSendsAsync(int portfolioId, string system)
    {
        await using var con = db.Create();
        try
        {
            var rows = await con.QueryAsync("""
                SELECT TOP 20 S.strSystem, S.strShape, S.dtmFrom, S.dtmTo, S.iJournals, S.iSent, S.strResult, S.dtmSent, U.strLoginEmail
                FROM tblAccountingSends S LEFT JOIN tblUsers U ON U.iUserId = S.iSentByUserID
                WHERE S.iUserID = @portfolioId AND S.strSystem = @system
                ORDER BY S.dtmSent DESC
                """, new { portfolioId, system });
            return rows.Cast<IDictionary<string, object?>>().Select(r => new AccountingSend(Db.Str(r, "strSystem"), Db.Str(r, "strShape"),
                Db.Date(r, "dtmFrom"), Db.Date(r, "dtmTo") ?? default, Db.Int(r, "iJournals") ?? 0, Db.Int(r, "iSent") ?? 0,
                Db.Str(r, "strResult") is { Length: > 0 } s ? s : null, r["dtmSent"] is DateTime d ? d : default,
                Db.Str(r, "strLoginEmail") is { Length: > 0 } e ? e : null)).ToList();
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208)
        {
            return [];
        }
    }

    /// <summary>Finishes the sign-in: swaps the code for tokens, finds the organisation and saves the connection.</summary>
    public async Task<ProcResult> ConnectAsync(int portfolioId, int loginUserId, string system, string code, string redirectUri, string? myobBusinessId)
    {
        try
        {
            var tokens = await api.ExchangeCodeAsync(system, code, redirectUri);
            var (id, name) = system == ExportSystems.Myob
                ? (myobBusinessId is { Length: > 0 } b ? b : throw new AccountingLinkException("MYOB didn't say which business was chosen. Connect again."), (string?)null)
                : await api.GetXeroTenantAsync(tokens);
            return await SaveAsync(portfolioId, system, id, name, tokens, loginUserId);
        }
        catch (Exception ex) when (ex is AccountingLinkException or HttpRequestException or TaskCanceledException or JsonException)
        {
            return ProcResult.Failed(ex is AccountingLinkException ? ex.Message : $"{system} couldn't be reached. Try again in a few minutes.");
        }
    }

    public async Task<ProcResult> DisconnectAsync(int portfolioId, string system)
    {
        await using var con = db.Create();
        return ProcResult.From(await con.ExecuteScalarAsync<string>("uspDeleteAccountingConnection",
            new { iUserID = portfolioId, strSystem = system }, commandType: CommandType.StoredProcedure), "Success");
    }

    /// <summary>Sends the journals, refreshing the sign-in first when it has run out, and records the send.</summary>
    public async Task<SendOutcome> SendAsync(int portfolioId, int loginUserId, string system, string shape, DateOnly? from, DateOnly to,
        IReadOnlyList<ExportJournal> journals)
    {
        if (await GetRowAsync(portfolioId, system) is not { } row || AccountingApi.LoadTokens(protector.Unprotect(row.Tokens)) is not { } tokens)
            return new SendOutcome(0, [$"Connect to {system} first."]);
        SendOutcome outcome;
        try
        {
            if (tokens.IsExpired(DateTime.UtcNow))
            {
                tokens = await api.RefreshAsync(system, tokens);
                await SaveAsync(portfolioId, system, row.OrganisationId, null, tokens, null);
            }
            outcome = system == ExportSystems.Myob
                ? await api.SendMyobAsync(tokens, row.OrganisationId, journals)
                : await api.SendXeroAsync(tokens, row.OrganisationId, journals);
        }
        catch (Exception ex) when (ex is AccountingLinkException or HttpRequestException or TaskCanceledException or JsonException)
        {
            outcome = new SendOutcome(0, [ex is AccountingLinkException ? ex.Message : $"{system} couldn't be reached. Try again in a few minutes."]);
        }
        await using var con = db.Create();
        await con.ExecuteScalarAsync<string>("uspAddAccountingSend", new
        {
            iUserID = portfolioId, strSystem = system, strShape = shape, dtFrom = from?.ToDateTime(TimeOnly.MinValue), dtTo = to.ToDateTime(TimeOnly.MinValue),
            iJournals = journals.Count, iSent = outcome.Sent, strResult = outcome.Ok ? null : string.Join(" ", outcome.Errors), iSentByUserID = loginUserId,
        }, commandType: CommandType.StoredProcedure);
        return outcome;
    }

    private async Task<ProcResult> SaveAsync(int portfolioId, string system, string id, string? name, AccountingTokens tokens, int? loginUserId)
    {
        await using var con = db.Create();
        return ProcResult.From(await con.ExecuteScalarAsync<string>("uspSaveAccountingConnection", new
        {
            iUserID = portfolioId, strSystem = system, strOrganisationID = id, strOrganisationName = name,
            strTokens = protector.Protect(AccountingApi.SaveTokens(tokens)), iConnectedByUserID = loginUserId,
        }, commandType: CommandType.StoredProcedure), "Success");
    }
}
