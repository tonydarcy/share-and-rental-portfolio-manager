using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PortfolioManager.Core.Prices;

public sealed class EodhdOptions
{
    /// <summary>Never committed: appsettings.Production.json next to the exe, or the Prices__Eodhd__ApiKey environment variable.</summary>
    public string ApiKey { get; set; } = "";
    public string BaseUrl { get; set; } = "https://eodhd.com/api/";
    /// <summary>Extra or replacement exchange mappings, app code to EODHD suffix (e.g. "CHIX": "AU").</summary>
    public Dictionary<string, string> ExchangeMap { get; set; } = [];
    /// <summary>Longest wait honoured from a 429 Retry-After header before retrying once.</summary>
    public int MaxRetryAfterSeconds { get; set; } = 60;
}

/// <summary>
/// End-of-day prices from EODHD (https://eodhd.com/financial-apis/api-for-historical-data-and-volumes):
/// GET {BaseUrl}eod/{TICKER}.{EXCHANGE}?api_token=..&amp;fmt=json&amp;period=d&amp;order=a&amp;from=yyyy-MM-dd&amp;to=yyyy-MM-dd,
/// one API call per request.
/// </summary>
public sealed partial class EodhdPriceProvider(HttpClient http, EodhdOptions options) : IPriceProvider, IAnnouncementProvider
{
    public string Name => "EODHD";

    /// <summary>App exchange codes (as stored in tblShareTrades / tblSharePrice) to EODHD exchange suffixes.</summary>
    public static readonly IReadOnlyDictionary<string, string> DefaultExchanges = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["ASX"] = "AU",
        ["NZX"] = "NZ",
        ["NYSE"] = "US",
        ["NASDAQ"] = "US",
        ["NYSEARCA"] = "US",
        ["NYSEAMERICAN"] = "US",
        ["AMEX"] = "US",
        ["BATS"] = "US",
        ["US"] = "US",
        ["LSE"] = "LSE",
        ["TSX"] = "TO",
        ["TSXV"] = "V",
        ["HKEX"] = "HK",
        ["SGX"] = "SG",
        ["NSE"] = "NSE",
        ["BSE"] = "BSE",
        ["XETRA"] = "XETRA",
        ["FOREX"] = "FOREX",
        ["CRYPTO"] = "CC",
    };

    /// <summary>EODHD symbol for an app symbol, e.g. ASX:BHP to BHP.AU and FOREX:USD/AUD to USDAUD.FOREX; null if it can't be mapped.</summary>
    public static string? ToProviderSymbol(PriceSymbol symbol, IReadOnlyDictionary<string, string>? exchangeMap = null)
    {
        var exchange = symbol.ExchangeCode.Trim();
        var code = symbol.StockCode.Trim().ToUpperInvariant();

        if (symbol.IsForex)
        {
            var pair = code.Replace("/", "");
            return CurrencyPair().IsMatch(pair) ? $"{pair}.FOREX" : null;
        }

        var suffix = exchangeMap?.FirstOrDefault(m => string.Equals(m.Key, exchange, StringComparison.OrdinalIgnoreCase)).Value
            ?? DefaultExchanges.GetValueOrDefault(exchange);
        if (string.IsNullOrWhiteSpace(suffix) || !Ticker().IsMatch(code))
            return null;

        // US class shares: BRK.B is BRK-B on EODHD
        if (suffix.Equals("US", StringComparison.OrdinalIgnoreCase))
            code = code.Replace('.', '-');
        return $"{code}.{suffix.Trim().ToUpperInvariant()}";
    }

    public async Task<PriceFetchResult> GetDailyPricesAsync(PriceSymbol symbol, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var (providerSymbol, failure) = Check(symbol);
        if (failure is not null)
            return failure;
        var query = $"&period=d&order=a&from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}";
        var (body, error) = await GetAsync("eod", providerSymbol!, query, ct);
        if (error is not null)
            return error;
        var result = Parse(body!);
        return result.IsOk ? result : result with { Message = $"{providerSymbol}: {result.Message}" };
    }

    /// <summary>
    /// Dividends with an ex-date on or after <paramref name="from"/>:
    /// GET {BaseUrl}div/{TICKER}.{EXCHANGE}?from=yyyy-MM-dd, one API call.
    /// </summary>
    public async Task<AnnouncementFetchResult<AnnouncedDividend>> GetDividendsAsync(PriceSymbol symbol, DateOnly from, CancellationToken ct = default)
    {
        var (providerSymbol, failure) = Check(symbol);
        if (failure is not null)
            return AnnouncementFetchResult<AnnouncedDividend>.Fail(failure.Status, failure.Message ?? "");
        var (body, error) = await GetAsync("div", providerSymbol!, $"&from={from:yyyy-MM-dd}", ct);
        if (error is not null)
            return AnnouncementFetchResult<AnnouncedDividend>.Fail(error.Status, error.Message ?? "");
        return ParseDividends(body!);
    }

    /// <summary>
    /// Splits and consolidations effective on or after <paramref name="from"/>:
    /// GET {BaseUrl}splits/{TICKER}.{EXCHANGE}?from=yyyy-MM-dd, one API call.
    /// </summary>
    public async Task<AnnouncementFetchResult<AnnouncedSplit>> GetSplitsAsync(PriceSymbol symbol, DateOnly from, CancellationToken ct = default)
    {
        var (providerSymbol, failure) = Check(symbol);
        if (failure is not null)
            return AnnouncementFetchResult<AnnouncedSplit>.Fail(failure.Status, failure.Message ?? "");
        var (body, error) = await GetAsync("splits", providerSymbol!, $"&from={from:yyyy-MM-dd}", ct);
        if (error is not null)
            return AnnouncementFetchResult<AnnouncedSplit>.Fail(error.Status, error.Message ?? "");
        return ParseSplits(body!);
    }

    /// <summary>
    /// What is inside an ETF: GET {BaseUrl}fundamentals/{TICKER}.{EXCHANGE}?filter=ETF_Data. Needs an EODHD plan with
    /// fundamentals (each call counts as 10). A stock that isn't a fund comes back Ok with no weights.
    /// </summary>
    public async Task<AnnouncementFetchResult<Reports.ExposureWeight>> GetFundExposureAsync(PriceSymbol symbol, CancellationToken ct = default)
    {
        var (providerSymbol, failure) = Check(symbol);
        if (failure is not null)
            return AnnouncementFetchResult<Reports.ExposureWeight>.Fail(failure.Status, failure.Message ?? "");
        var (body, error) = await GetAsync("fundamentals", providerSymbol!, "&filter=ETF_Data", ct);
        if (error is not null)
            return AnnouncementFetchResult<Reports.ExposureWeight>.Fail(error.Status, error.Message ?? "");
        return ParseFundExposure(body!);
    }

    /// <summary>
    /// Reads EODHD's ETF_Data: {"Sector_Weights":{"Financial Services":{"Equity_%":"31.2"},...},
    /// "World_Regions":{"Australasia":{"Equity_%":"98.1"},...},
    /// "Top_10_Holdings":{"CBA.AU":{"Code":"CBA","Name":"Commonwealth Bank","Assets_%":10.1},...}}.
    /// Percentages may be numbers or text. Anything else (not a fund) is no weights.
    /// </summary>
    public static AnnouncementFetchResult<Reports.ExposureWeight> ParseFundExposure(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return AnnouncementFetchResult<Reports.ExposureWeight>.Ok([]); }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return AnnouncementFetchResult<Reports.ExposureWeight>.Ok([]);
            var weights = new List<Reports.ExposureWeight>();
            void Read(string section, string kind, string percent)
            {
                if (!root.TryGetProperty(section, out var el) || el.ValueKind != JsonValueKind.Object)
                    return;
                foreach (var item in el.EnumerateObject())
                {
                    if (item.Value.ValueKind != JsonValueKind.Object || Number(item.Value, percent) is not { } pct || pct <= 0)
                        continue;
                    var name = kind == Reports.LookThrough.Holding ? (Text(item.Value, "Code") ?? item.Name.Split('.')[0]).ToUpperInvariant() : item.Name.Trim();
                    var label = kind == Reports.LookThrough.Holding ? Text(item.Value, "Name") : null;
                    if (name.Length > 0 && weights.All(w => w.Kind != kind || !w.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                        weights.Add(new Reports.ExposureWeight(kind, name, Math.Min(1m, (decimal)pct / 100), label));
                }
            }
            Read("Sector_Weights", Reports.LookThrough.Sector, "Equity_%");
            Read("World_Regions", Reports.LookThrough.Country, "Equity_%");
            Read("Top_10_Holdings", Reports.LookThrough.Holding, "Assets_%");
            return AnnouncementFetchResult<Reports.ExposureWeight>.Ok(weights);
        }
    }

    private (string? ProviderSymbol, PriceFetchResult? Failure) Check(PriceSymbol symbol)
    {
        var providerSymbol = ToProviderSymbol(symbol, options.ExchangeMap);
        if (providerSymbol is null)
            return (null, PriceFetchResult.Fail(PriceFetchStatus.NotSupported, $"No EODHD symbol for {symbol}; add the exchange to Prices:Eodhd:ExchangeMap."));
        if (string.IsNullOrWhiteSpace(options.ApiKey))
            return (null, PriceFetchResult.Fail(PriceFetchStatus.Unauthorized, "The EODHD API key is not set (Prices:Eodhd:ApiKey)."));
        return (providerSymbol, null);
    }

    /// <summary>One GET, retried once after a 429. Returns the body, or the failure (with the symbol in its message).</summary>
    private async Task<(string? Body, PriceFetchResult? Error)> GetAsync(string endpoint, string providerSymbol, string query, CancellationToken ct)
    {
        var url = $"{options.BaseUrl.TrimEnd('/')}/{endpoint}/{Uri.EscapeDataString(providerSymbol)}" +
                  $"?api_token={Uri.EscapeDataString(options.ApiKey)}&fmt=json{query}";

        for (var attempt = 1; ; attempt++)
        {
            PriceFetchResult result;
            try
            {
                using var response = await http.GetAsync(url, ct);
                var body = await response.Content.ReadAsStringAsync(ct);
                if (response.IsSuccessStatusCode)
                    return (body, null);
                result = FromStatus(response.StatusCode, body, response.Headers.RetryAfter?.Delta);
            }
            catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                // Never let the URL (and so the key) reach the log
                result = PriceFetchResult.Fail(PriceFetchStatus.Failed, Redact(ex.Message));
            }

            if (result.Status != PriceFetchStatus.RateLimited || attempt > 1)
                return (null, result with { Message = $"{providerSymbol}: {result.Message}" });

            var wait = result.RetryAfter ?? TimeSpan.FromSeconds(5);
            if (wait > TimeSpan.FromSeconds(options.MaxRetryAfterSeconds))
                wait = TimeSpan.FromSeconds(options.MaxRetryAfterSeconds);
            await Task.Delay(wait, ct);
        }
    }

    /// <summary>
    /// Reads EODHD's dividends: [{"date":"2024-02-21","declarationDate":"2024-02-14","recordDate":"2024-02-22",
    /// "paymentDate":"2024-03-28","period":"Interim","value":2.15,"unadjustedValue":2.15,"currency":"AUD"}, ...].
    /// "date" is the ex-date. Uses unadjustedValue (per unit as paid, not adjusted for later splits) when there is one.
    /// </summary>
    public static AnnouncementFetchResult<AnnouncedDividend> ParseDividends(string json) =>
        ParseArray(json, row =>
        {
            if (DateField(row, "date") is not { } exDate)
                return null;
            var amount = Number(row, "unadjustedValue") is > 0 and var u ? u : Number(row, "value");
            if (amount is not > 0)
                return null;
            return new AnnouncedDividend(exDate, DateField(row, "recordDate"), DateField(row, "paymentDate"),
                DateField(row, "declarationDate"), (decimal)amount.Value, Text(row, "currency")?.ToUpperInvariant(), Text(row, "period"));
        }, d => d.ExDate);

    /// <summary>Reads EODHD's splits: [{"date":"2020-08-31","split":"4.000000/1.000000"}, ...], new units / old units.</summary>
    public static AnnouncementFetchResult<AnnouncedSplit> ParseSplits(string json) =>
        ParseArray(json, row =>
            DateField(row, "date") is { } date && SplitText(Text(row, "split")) is { } ratio
                ? new AnnouncedSplit(date, ratio.NewUnits, ratio.OldUnits)
                : null,
            s => s.EffectiveDate);

    /// <summary>"4.000000/1.000000" as 4 for 1 and "1.5/1" as 3 for 2; null if it isn't a positive ratio.</summary>
    public static (int NewUnits, int OldUnits)? SplitText(string? text)
    {
        var parts = (text ?? "").Split('/', StringSplitOptions.TrimEntries);
        if (parts.Length != 2
            || !decimal.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
            || !decimal.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var o)
            || n <= 0 || o <= 0 || n == o)
            return null;
        // Scale to whole numbers (up to 6 decimal places), then reduce
        for (var i = 0; i < 6 && (n != decimal.Truncate(n) || o != decimal.Truncate(o)); i++)
        {
            n *= 10;
            o *= 10;
        }
        if (n != decimal.Truncate(n) || o != decimal.Truncate(o) || n > int.MaxValue || o > int.MaxValue)
            return null;
        var gcd = Gcd((long)n, (long)o);
        return ((int)(n / gcd), (int)(o / gcd));
    }

    private static long Gcd(long a, long b) => b == 0 ? a : Gcd(b, a % b);

    private static AnnouncementFetchResult<T> ParseArray<T>(string json, Func<JsonElement, T?> read, Func<T, DateOnly> key) where T : class
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return AnnouncementFetchResult<T>.Fail(PriceFetchStatus.Failed, "Response was not JSON."); }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return AnnouncementFetchResult<T>.Fail(PriceFetchStatus.Failed, ErrorText(doc.RootElement) ?? "Unexpected response.");
            var items = new Dictionary<DateOnly, T>();
            foreach (var row in doc.RootElement.EnumerateArray())
                if (row.ValueKind == JsonValueKind.Object && read(row) is { } item)
                    items[key(item)] = item;
            return AnnouncementFetchResult<T>.Ok(items.OrderBy(i => i.Key).Select(i => i.Value).ToList());
        }
    }

    private static DateOnly? DateField(JsonElement row, string name) =>
        row.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
        && DateOnly.TryParseExact(el.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d : null;

    private static string? Text(JsonElement row, string name) =>
        row.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(el.GetString())
            ? el.GetString()!.Trim() : null;

    /// <summary>
    /// Reads the JSON array EODHD returns:
    /// [{"date":"2024-01-02","open":1.0,"high":1.0,"low":1.0,"close":1.0,"adjusted_close":1.0,"volume":100}, ...].
    /// Uses the raw close (like the legacy CSV files), skips rows with no close, oldest first.
    /// </summary>
    public static PriceFetchResult Parse(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return PriceFetchResult.Fail(PriceFetchStatus.Failed, "Response was not JSON."); }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return PriceFetchResult.Fail(PriceFetchStatus.Failed, ErrorText(doc.RootElement) ?? "Unexpected response.");

            var prices = new Dictionary<DateOnly, DailyPrice>();
            foreach (var row in doc.RootElement.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object
                    || !row.TryGetProperty("date", out var dateEl) || dateEl.ValueKind != JsonValueKind.String
                    || !DateOnly.TryParseExact(dateEl.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    continue;

                var close = Number(row, "close");
                if (close is not > 0)
                    continue;

                prices[date] = new DailyPrice(date, Number(row, "open"), Number(row, "high"), Number(row, "low"), close.Value, Number(row, "volume"));
            }
            return PriceFetchResult.Ok(prices.Values.OrderBy(p => p.Date).ToList());
        }
    }

    public static PriceFetchResult FromStatus(HttpStatusCode status, string body, TimeSpan? retryAfter = null)
    {
        var detail = BodyText(body);
        return (int)status switch
        {
            401 => PriceFetchResult.Fail(PriceFetchStatus.Unauthorized, $"API key rejected (401). {detail}".Trim()),
            402 => PriceFetchResult.Fail(PriceFetchStatus.QuotaExceeded, $"Daily API limit reached (402). {detail}".Trim()),
            403 => PriceFetchResult.Fail(PriceFetchStatus.NotEntitled, $"Not included in the EODHD plan (403). {detail}".Trim()),
            404 => PriceFetchResult.Fail(PriceFetchStatus.NotFound, $"Ticker not found (404). {detail}".Trim()),
            429 => PriceFetchResult.Fail(PriceFetchStatus.RateLimited, $"Too many requests (429). {detail}".Trim(), retryAfter),
            _ => PriceFetchResult.Fail(PriceFetchStatus.Failed, $"HTTP {(int)status}. {detail}".Trim()),
        };
    }

    private static double? Number(JsonElement row, string name)
    {
        if (!row.TryGetProperty(name, out var el)) return null;
        return el.ValueKind switch
        {
            JsonValueKind.Number when el.TryGetDouble(out var d) => d,
            JsonValueKind.String when double.TryParse(el.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
            _ => null,
        };
    }

    private static string? ErrorText(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object)
            foreach (var name in new[] { "message", "error", "errors" })
                if (root.TryGetProperty(name, out var el))
                    return el.ValueKind == JsonValueKind.String ? el.GetString() : el.GetRawText();
        return null;
    }

    private static string BodyText(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (ErrorText(doc.RootElement) is { } text) return Short(text);
        }
        catch (JsonException) { }
        return Short(body.Trim());
    }

    private static string Short(string s) => s.Length <= 200 ? s : s[..200];

    private string Redact(string message) =>
        string.IsNullOrEmpty(options.ApiKey) ? message : message.Replace(options.ApiKey, "***").Replace(Uri.EscapeDataString(options.ApiKey), "***");

    [GeneratedRegex("^[A-Z]{6}$")]
    private static partial Regex CurrencyPair();

    [GeneratedRegex("^[A-Z0-9][A-Z0-9.\\-]{0,19}$")]
    private static partial Regex Ticker();
}
