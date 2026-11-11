using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace PortfolioManager.Mobile.Client;

public sealed record PortfolioSummary(int PortfolioId, string Name, string? EntityType, string? Role, bool CanEdit);

public sealed record Holding(
    string StockCode, string ExchangeCode, decimal Units, decimal Price, decimal MarketValue,
    decimal CapitalGain, decimal Payouts, decimal TotalProfit, string? Trend, decimal Cost);

public sealed record HoldingsTotal(decimal MarketValue, decimal CapitalGain, decimal Payouts, decimal TotalProfit, decimal Cost);

public sealed record HoldingsSummary(IReadOnlyList<Holding> Holdings, HoldingsTotal Total);

public sealed record DayMove(DateOnly Date, decimal Change, double Return);

public sealed record TopHolding(
    string ExchangeCode, string StockCode, decimal Units, decimal Price, decimal MarketValue, decimal CapitalGain,
    decimal? GainPercent, decimal Weight);

public sealed record ExpectedPayout(string StockExchangeCode, string StockCode, DateTime ProjectedPaymentDate, decimal ProjectedAmount);

/// <summary>The dashboard's headline figures (the get_dashboard tool).</summary>
public sealed record DashboardSummary(
    string? Currency, decimal TotalPortfolioValue, decimal TotalShareValue, decimal TotalCashValue, DayMove? LatestMove,
    HoldingsTotal Holdings, IReadOnlyList<TopHolding> TopHoldings, IReadOnlyList<ExpectedPayout> UpcomingPayouts);

public sealed record PricePoint(DateOnly Date, double Close, double? Open, double? High, double? Low, double? Volume);

/// <summary>One share's daily prices and quote (the get_price_history tool).</summary>
public sealed record PriceHistory(
    string Exchange, string Code, string? Name, string? Sector, decimal? UnitsHeld, double? Latest, DateOnly? LatestDate,
    double? Change, double? ChangePct, double? YearLow, double? YearHigh, IReadOnlyList<PricePoint> Prices);

public sealed record PayoutRow(
    int PayoutId, string StockExchangeCode, string StockCode, DateTime? Date, decimal Total, decimal Franked, decimal Unfranked,
    decimal FrankingCredit, decimal Interest, decimal TrustIncome, decimal TaxWithheld, decimal ForeignTax, decimal Other,
    string? Comment);

public sealed record PayoutTotals(
    decimal Total, decimal Franked, decimal Unfranked, decimal FrankingCredit, decimal Interest,
    decimal TrustIncome, decimal TaxWithheld, decimal ForeignTax, decimal Other);

public sealed record PayoutPage(int Total, int Returned, IReadOnlyList<PayoutRow> Rows);

/// <summary>Payouts in a period, newest first, with the period's totals (the list_payouts tool).</summary>
public sealed record PayoutList(PayoutTotals? Totals, PayoutPage Payouts);

/// <summary>
/// The site's JSON API (/api/v1), signed in with the key from <see cref="SignInClient"/>. With an
/// <see cref="OfflineCopy"/>, every reply is kept on the phone and shown again when the site can't be reached.
/// </summary>
public sealed class SiteApi(HttpClient http, string server, string key, OfflineCopy? offline = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>When any reply so far came from the offline copy instead of the site: the oldest such copy's time.</summary>
    public DateTimeOffset? OfflineCopySaved { get; private set; }

    public Task<List<PortfolioSummary>> ListPortfoliosAsync(CancellationToken ct = default) =>
        GetAsync<List<PortfolioSummary>>("/portfolios", ct);

    public Task<HoldingsSummary> GetHoldingsAsync(int portfolioId, CancellationToken ct = default) =>
        GetAsync<HoldingsSummary>($"/portfolios/{portfolioId}/holdings", ct);

    public Task<DashboardSummary> GetDashboardAsync(int portfolioId, CancellationToken ct = default) =>
        GetAsync<DashboardSummary>($"/portfolios/{portfolioId}/dashboard", ct);

    public Task<PriceHistory> GetPriceHistoryAsync(int portfolioId, string exchange, string stockCode, DateOnly? from = null, CancellationToken ct = default) =>
        GetAsync<PriceHistory>($"/portfolios/{portfolioId}/prices/{Uri.EscapeDataString(exchange)}/{Uri.EscapeDataString(stockCode)}"
            + (from is { } f ? $"?from={f:yyyy-MM-dd}" : ""), ct);

    public Task<PayoutList> ListPayoutsAsync(int portfolioId, DateOnly from, DateOnly to, int limit = 500, CancellationToken ct = default) =>
        GetAsync<PayoutList>($"/portfolios/{portfolioId}/payouts?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}&limit={limit}", ct);

    public Task<List<SecurityMatch>> SearchSecuritiesAsync(int portfolioId, string query, CancellationToken ct = default) =>
        GetAsync<List<SecurityMatch>>($"/portfolios/{portfolioId}/securities?query={Uri.EscapeDataString(query)}", ct);

    // Writes go straight to the site: never from or into the offline copy, and never retried by the app

    public Task<SavedReply> AddTradeAsync(int portfolioId, TradeRequest trade, CancellationToken ct = default) =>
        PostAsync($"/portfolios/{portfolioId}/trades", trade, ct);

    public Task<SavedReply> AddPayoutAsync(int portfolioId, PayoutRequest payout, CancellationToken ct = default) =>
        PostAsync($"/portfolios/{portfolioId}/payouts", payout, ct);

    public Task<SavedReply> AddCashAsync(int portfolioId, CashRequest cash, CancellationToken ct = default) =>
        PostAsync($"/portfolios/{portfolioId}/cash", cash, ct);

    public Task<SavedReply> AddAttachmentAsync(int portfolioId, AttachmentRequest file, CancellationToken ct = default) =>
        PostAsync($"/portfolios/{portfolioId}/attachments", file, ct);

    private async Task<SavedReply> PostAsync<TBody>(string path, TBody body, CancellationToken ct)
    {
        var json = await FetchAsync(path, ct, JsonContent.Create(body, options: Json));
        try
        {
            return JsonSerializer.Deserialize<SavedReply>(json, Json) ?? new SavedReply(null, null);
        }
        catch (JsonException)
        {
            return new SavedReply(null, null); // saved; the reply just wasn't in the expected shape
        }
    }

    private async Task<T> GetAsync<T>(string path, CancellationToken ct)
    {
        string json;
        var fromCopy = false;
        try
        {
            json = await FetchAsync(path, ct);
        }
        catch (SiteException e) when (e.Unreachable && offline?.Load(server + path) is { } copy)
        {
            fromCopy = true;
            OfflineCopySaved = OfflineCopySaved is { } earlier && earlier < copy.Saved ? earlier : copy.Saved;
            json = copy.Json;
        }
        try
        {
            var value = JsonSerializer.Deserialize<T>(json, Json) ?? throw new SiteException("The site sent an empty reply.");
            if (!fromCopy)
                offline?.Save(server + path, json);
            return value;
        }
        catch (JsonException)
        {
            throw new SiteException("The site sent an unexpected reply.");
        }
    }

    private async Task<string> FetchAsync(string path, CancellationToken ct, HttpContent? body = null)
    {
        using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, server + "/api/v1" + path) { Content = body };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        HttpResponseMessage reply;
        try
        {
            reply = await http.SendAsync(request, ct);
        }
        catch (HttpRequestException)
        {
            throw new SiteException("Can't reach the site. Check your internet connection.", unreachable: true);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new SiteException(body is null
                ? "The site took too long to reply. Please try again."
                : "The site took too long to reply, so this may or may not have been saved. Check the site before adding it again.",
                unreachable: true);
        }
        using (reply)
        {
            if (reply.StatusCode is HttpStatusCode.Unauthorized)
                throw new SiteException("You've been signed out. Please sign in again.", signedOut: true);
            if (!reply.IsSuccessStatusCode)
                throw new SiteException(await ErrorAsync(reply, ct));
            return await reply.Content.ReadAsStringAsync(ct);
        }
    }

    /// <summary>The site's own words for a refused call ({"error": "..."}), or the status when there are none.</summary>
    private static async Task<string> ErrorAsync(HttpResponseMessage reply, CancellationToken ct)
    {
        try
        {
            var body = await reply.Content.ReadFromJsonAsync<JsonElement>(ct);
            if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("error", out var e) && e.GetString() is { Length: > 0 } s)
                return s;
        }
        catch (JsonException)
        {
        }
        return reply.StatusCode == HttpStatusCode.TooManyRequests
            ? "Too many requests. Wait a minute and try again."
            : $"The site couldn't do that ({(int)reply.StatusCode}).";
    }
}
