using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PortfolioManager.Core.NavexaSync;

public sealed class NavexaApiPortfolio
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? BaseCurrencyCode { get; set; }
    public bool IsPortfolioGroup { get; set; }
}

/// <summary>One event from POST /v1/portfolios/{id}/transactions: a trade, dividend, cash transaction and so on.</summary>
public sealed class NavexaApiTransaction
{
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string Id { get; set; } = "";
    /// <summary>trade, cryptoTrade, dividend, cryptoIncome, cashTransaction or merger.</summary>
    public string Type { get; set; } = "";
    public DateTime Date { get; set; }
    public int? TradeTypeId { get; set; }
    public string? TradeType { get; set; }
    public decimal? Value { get; set; }
    public string? Currency { get; set; }
    public decimal? BaseValue { get; set; }
    public string? BaseCurrency { get; set; }
    public decimal? ExchangeRate { get; set; }
    public List<NavexaApiLeg>? Incoming { get; set; }
    public List<NavexaApiLeg>? Outgoing { get; set; }
    public List<NavexaApiLeg>? Fees { get; set; }
    public NavexaApiDividend? Dividend { get; set; }
    public NavexaApiLink? LinkedTo { get; set; }
    public string? Notes { get; set; }
    public bool IsConfirmed { get; set; }
    public bool AutoAdded { get; set; }
    public DateTime? LastModifiedDate { get; set; }

    [JsonIgnore]
    public IEnumerable<NavexaApiLeg> Legs => (Incoming ?? []).Concat(Outgoing ?? []);
}

public sealed class NavexaApiLeg
{
    public NavexaApiHolding? Holding { get; set; }
    public string? Currency { get; set; }
    public decimal? Quantity { get; set; }
    public decimal? Price { get; set; }
    public decimal? Amount { get; set; }
    public decimal? BaseAmount { get; set; }
    public int? TradeTypeId { get; set; }
    public string? TradeType { get; set; }
    public decimal? Brokerage { get; set; }
    public string? BrokerageCurrency { get; set; }
}

public sealed class NavexaApiHolding
{
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string Id { get; set; } = "";
    public string Symbol { get; set; } = "";
    public string? Name { get; set; }
    public string? Exchange { get; set; }
    /// <summary>STOCK, CRYPTO, CASH or CUSTOM.</summary>
    public string? HoldingType { get; set; }
}

public sealed class NavexaApiDividend
{
    public decimal? GrossAmount { get; set; }
    public decimal? NetAmount { get; set; }
    public decimal? Franked { get; set; }
    public decimal? Unfranked { get; set; }
    public decimal? FrankingCredits { get; set; }
    public bool IsForeignIncome { get; set; }
    public decimal? ForeignTaxWithheld { get; set; }
    public bool IsReinvested { get; set; }
    public DateTime? ExDividendDate { get; set; }
    public DateTime? PaymentDate { get; set; }
}

public sealed class NavexaApiLink
{
    public string? Type { get; set; }
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? Id { get; set; }
}

/// <summary>A call Navexa refused or that failed, with a message fit to show the user.</summary>
public sealed class NavexaApiException(string message, HttpStatusCode? status = null) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;
}

/// <summary>
/// Reads a Navexa account through its public API (https://developer.navexa.com) with the user's own API key.
/// Read-only: it never changes anything in Navexa.
/// </summary>
public sealed class NavexaApiClient(HttpClient http)
{
    /// <summary>The address in Navexa's quick start (https://developer.navexa.com). Some of their pages add /api/, so that is tried on a 404.</summary>
    public static readonly Uri DefaultBaseAddress = new("https://api.navexa.com.au/");
    public const int PageSize = 250;
    private const int MaxPages = 400;
    private const int MaxRateLimitRetries = 3;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // The guide says x-api-key; the endpoint pages show the key in Authorization. Try the first, fall back to the second.
    private bool _useAuthorizationHeader;
    // Navexa's docs disagree on whether paths start /v1 or /api/v1, and a wrong one answers 404. Each way is tried once.
    private bool _useAlternateBase;
    private bool _baseConfirmed;

    public async Task<IReadOnlyList<NavexaApiPortfolio>> GetPortfoliosAsync(string apiKey, CancellationToken ct = default)
    {
        var body = await SendAsync<JsonElement>(apiKey, () => new HttpRequestMessage(HttpMethod.Get, "v1/portfolios"), ct,
            "Navexa's API answered \"not found\" when asked for your portfolios. Check the API key, or try again later.");
        return ReadPortfolios(body);
    }

    /// <summary>A plain list, as the docs show, or the grouped shape { portfolios, sharedPortfolios, portfolioGroups }.</summary>
    public static IReadOnlyList<NavexaApiPortfolio> ReadPortfolios(JsonElement body)
    {
        IEnumerable<JsonElement> items = body.ValueKind switch
        {
            JsonValueKind.Array => body.EnumerateArray(),
            JsonValueKind.Object => new[] { "portfolios", "sharedPortfolios" }
                .Where(n => body.TryGetProperty(n, out var a) && a.ValueKind == JsonValueKind.Array)
                .SelectMany(n => body.GetProperty(n).EnumerateArray()),
            _ => [],
        };
        return items.Select(e => e.Deserialize<NavexaApiPortfolio>(Json)!)
            .Where(p => !p.IsPortfolioGroup).DistinctBy(p => p.Id).OrderBy(p => p.Name).ToList();
    }

    /// <summary>Every event in the portfolio, oldest first, one page of 250 at a time.</summary>
    public async Task<IReadOnlyList<NavexaApiTransaction>> GetTransactionsAsync(string apiKey, int navexaPortfolioId, DateOnly to,
        CancellationToken ct = default)
    {
        var all = new List<NavexaApiTransaction>();
        for (var page = 1; page <= MaxPages; page++)
        {
            var body = JsonSerializer.Serialize(new
            {
                isPortfolioGroup = false,
                from = "1960-01-01",
                to = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                page,
                pageSize = PageSize,
                sortBy = "date",
                sortDirection = "asc",
            }, Json);
            var result = await SendAsync<TransactionPage>(apiKey, () => new HttpRequestMessage(HttpMethod.Post,
                $"v1/portfolios/{navexaPortfolioId}/transactions")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json-patch+json"),
            }, ct, $"Navexa couldn't find portfolio {navexaPortfolioId} with this API key. Disconnect, then connect and choose it again.");
            var items = result?.Items ?? [];
            all.AddRange(items);
            if (items.Count == 0 || (result?.Total is { } total && all.Count >= total))
                break;
        }
        return all;
    }

    private sealed class TransactionPage
    {
        public int? Total { get; set; }
        public List<NavexaApiTransaction>? Items { get; set; }
    }

    private async Task<T?> SendAsync<T>(string apiKey, Func<HttpRequestMessage> build, CancellationToken ct, string notFoundMessage)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new NavexaApiException("There is no Navexa API key saved.");

        var triedOtherBase = false;
        for (var attempt = 0; ; attempt++)
        {
            using var request = build();
            request.RequestUri = Resolve(request.RequestUri!);
            if (_useAuthorizationHeader)
                request.Headers.TryAddWithoutValidation("Authorization", apiKey.Trim());
            else
                request.Headers.Add("x-api-key", apiKey.Trim());
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, ct);
            }
            catch (HttpRequestException ex)
            {
                throw new NavexaApiException($"Couldn't reach Navexa: {ex.Message}");
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new NavexaApiException("Navexa took too long to answer. Try again later.");
            }

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.Unauthorized && !_useAuthorizationHeader)
                {
                    _useAuthorizationHeader = true;
                    continue;
                }
                if (response.StatusCode == HttpStatusCode.NotFound && !_baseConfirmed && !triedOtherBase)
                {
                    triedOtherBase = true;
                    _useAlternateBase = !_useAlternateBase;
                    continue;
                }
                if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < MaxRateLimitRetries)
                {
                    await Task.Delay(RetryDelay(response, attempt), ct);
                    continue;
                }
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    if (triedOtherBase)
                        _useAlternateBase = !_useAlternateBase;
                    throw new NavexaApiException(notFoundMessage, response.StatusCode);
                }
                if (!response.IsSuccessStatusCode)
                    throw new NavexaApiException(FailureMessage(response.StatusCode), response.StatusCode);

                _baseConfirmed = true;
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                try
                {
                    return await JsonSerializer.DeserializeAsync<T>(stream, Json, ct);
                }
                catch (JsonException)
                {
                    throw new NavexaApiException("Navexa sent back something this site couldn't read. Try again later.");
                }
            }
        }
    }

    /// <summary>The request's address on the base in use: the configured one, or the same with /api/ added or taken off.</summary>
    private Uri Resolve(Uri relative)
    {
        if (relative.IsAbsoluteUri)
            return relative;
        var configured = http.BaseAddress ?? DefaultBaseAddress;
        return new Uri(_useAlternateBase ? AlternateBase(configured) : configured, relative);
    }

    public static Uri AlternateBase(Uri baseAddress)
    {
        var text = baseAddress.ToString().TrimEnd('/') + "/";
        return new Uri(text.EndsWith("/api/", StringComparison.OrdinalIgnoreCase) ? text[..^4] : text + "api/");
    }

    /// <summary>Navexa's Retry-After, capped at a minute so a page request doesn't hang; doubles when it is missing.</summary>
    public static TimeSpan RetryDelay(HttpResponseMessage response, int attempt)
    {
        var seconds = response.Headers.RetryAfter?.Delta?.TotalSeconds ?? 5 * Math.Pow(2, attempt);
        return TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 60));
    }

    public static string FailureMessage(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized => "Navexa didn't accept the API key. Check it in Navexa under Settings, API keys, and save it again.",
        HttpStatusCode.Forbidden => "Navexa refused access. Your Navexa plan may not include API access to this portfolio.",
        HttpStatusCode.TooManyRequests => "Navexa's hourly API limit has been reached. Try again in an hour.",
        _ => $"Navexa answered with an error ({(int)status}). Try again later.",
    };
}

/// <summary>Reads an id that may come as a JSON number or a string.</summary>
public sealed class FlexibleStringConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
    {
        JsonTokenType.String => reader.GetString(),
        JsonTokenType.Number => reader.TryGetInt64(out var l) ? l.ToString(CultureInfo.InvariantCulture)
            : reader.GetDecimal().ToString(CultureInfo.InvariantCulture),
        JsonTokenType.Null => null,
        _ => throw new JsonException("Expected a string or a number."),
    };

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);
}
