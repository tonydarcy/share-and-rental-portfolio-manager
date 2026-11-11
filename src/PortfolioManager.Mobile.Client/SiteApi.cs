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

/// <summary>The site's JSON API (/api/v1), signed in with the key from <see cref="SignInClient"/>.</summary>
public sealed class SiteApi(HttpClient http, string server, string key)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<List<PortfolioSummary>> ListPortfoliosAsync(CancellationToken ct = default) =>
        GetAsync<List<PortfolioSummary>>("/portfolios", ct);

    public Task<HoldingsSummary> GetHoldingsAsync(int portfolioId, CancellationToken ct = default) =>
        GetAsync<HoldingsSummary>($"/portfolios/{portfolioId}/holdings", ct);

    private async Task<T> GetAsync<T>(string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, server + "/api/v1" + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        HttpResponseMessage reply;
        try
        {
            reply = await http.SendAsync(request, ct);
        }
        catch (HttpRequestException)
        {
            throw new SiteException("Can't reach the site. Check your internet connection.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new SiteException("The site took too long to reply. Please try again.");
        }
        using (reply)
        {
            if (reply.StatusCode is HttpStatusCode.Unauthorized)
                throw new SiteException("You've been signed out. Please sign in again.", signedOut: true);
            if (!reply.IsSuccessStatusCode)
                throw new SiteException(await ErrorAsync(reply, ct));
            try
            {
                return await reply.Content.ReadFromJsonAsync<T>(Json, ct) ?? throw new SiteException("The site sent an empty reply.");
            }
            catch (JsonException)
            {
                throw new SiteException("The site sent an unexpected reply.");
            }
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
