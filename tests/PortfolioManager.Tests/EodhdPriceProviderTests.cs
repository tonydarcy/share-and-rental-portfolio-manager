using System.Net;
using PortfolioManager.Core.Prices;

namespace PortfolioManager.Tests;

public class EodhdPriceProviderTests
{
    private const string TwoDays = """
        [
          {"date":"2026-10-14","open":45.1,"high":45.9,"low":44.8,"close":45.62,"adjusted_close":44.1,"volume":8123456},
          {"date":"2026-10-13","open":44.0,"high":45.2,"low":43.9,"close":45.0,"adjusted_close":43.5,"volume":7000000}
        ]
        """;

    [Theory]
    [InlineData("ASX", "BHP", "BHP.AU")]
    [InlineData("asx", " cba ", "CBA.AU")]
    [InlineData("NASDAQ", "AAPL", "AAPL.US")]
    [InlineData("NYSE", "BRK.B", "BRK-B.US")]
    [InlineData("LSE", "VOD", "VOD.LSE")]
    [InlineData("NSE", "RELIANCE", "RELIANCE.NSE")]
    [InlineData("BSE", "TCS", "TCS.BSE")]
    [InlineData("FOREX", "USD/AUD", "USDAUD.FOREX")]
    [InlineData("Forex", "usdaud", "USDAUD.FOREX")]
    public void Maps_app_symbols_to_eodhd(string exchange, string code, string expected) =>
        Assert.Equal(expected, EodhdPriceProvider.ToProviderSymbol(new PriceSymbol(exchange, code)));

    [Theory]
    [InlineData("MADEUP", "ABC")]
    [InlineData("ASX", "")]
    [InlineData("ASX", "BHP&x=1")]
    [InlineData("FOREX", "USD/AU")]
    [InlineData("FOREX", "USD-1/AUD")]
    public void Unmappable_symbols_give_null(string exchange, string code) =>
        Assert.Null(EodhdPriceProvider.ToProviderSymbol(new PriceSymbol(exchange, code)));

    [Fact]
    public void Exchange_map_from_config_adds_and_overrides()
    {
        var map = new Dictionary<string, string> { ["chix"] = "AU", ["ASX"] = "XASX" };
        Assert.Equal("ABC.AU", EodhdPriceProvider.ToProviderSymbol(new PriceSymbol("CHIX", "ABC"), map));
        Assert.Equal("BHP.XASX", EodhdPriceProvider.ToProviderSymbol(new PriceSymbol("ASX", "BHP"), map));
    }

    [Fact]
    public void Parses_rows_oldest_first_using_the_raw_close()
    {
        var result = EodhdPriceProvider.Parse(TwoDays);

        Assert.True(result.IsOk);
        Assert.Equal([new DateOnly(2026, 10, 13), new DateOnly(2026, 10, 14)], result.Prices.Select(p => p.Date));
        var last = result.Prices[^1];
        Assert.Equal(45.62, last.Close);
        Assert.Equal(45.1, last.Open);
        Assert.Equal(45.9, last.High);
        Assert.Equal(44.8, last.Low);
        Assert.Equal(8123456d, last.Volume);
    }

    [Fact]
    public void Skips_rows_without_a_usable_close_or_date_and_reads_numbers_sent_as_text()
    {
        var result = EodhdPriceProvider.Parse("""
            [
              {"date":"2026-10-12","open":null,"high":null,"low":null,"close":null,"volume":0},
              {"date":"2026-10-13","close":0,"volume":0},
              {"date":"not a date","close":1.5},
              {"close":1.5},
              {"date":"2026-10-14","open":"0.6612","high":"0.6650","low":"0.6590","close":"0.6631","volume":null}
            ]
            """);

        var only = Assert.Single(result.Prices);
        Assert.Equal(new DateOnly(2026, 10, 14), only.Date);
        Assert.Equal(0.6631, only.Close);
        Assert.Equal(0.6612, only.Open);
        Assert.Null(only.Volume);
    }

    [Fact]
    public void Empty_array_is_ok_with_no_prices()
    {
        var result = EodhdPriceProvider.Parse("[]");
        Assert.True(result.IsOk);
        Assert.Empty(result.Prices);
    }

    [Theory]
    [InlineData("""{"message":"Something went wrong"}""", "Something went wrong")]
    [InlineData("Ticker Not Found.", "Response was not JSON.")]
    public void Non_array_bodies_are_failures(string body, string message)
    {
        var result = EodhdPriceProvider.Parse(body);
        Assert.Equal(PriceFetchStatus.Failed, result.Status);
        Assert.Equal(message, result.Message);
    }

    [Theory]
    [InlineData(401, PriceFetchStatus.Unauthorized, true)]
    [InlineData(402, PriceFetchStatus.QuotaExceeded, true)]
    [InlineData(403, PriceFetchStatus.NotEntitled, false)]
    [InlineData(404, PriceFetchStatus.NotFound, false)]
    [InlineData(429, PriceFetchStatus.RateLimited, false)]
    [InlineData(500, PriceFetchStatus.Failed, false)]
    public void Maps_http_errors(int status, PriceFetchStatus expected, bool stopsRun)
    {
        var result = EodhdPriceProvider.FromStatus((HttpStatusCode)status, "Ticker Not Found.");
        Assert.Equal(expected, result.Status);
        Assert.Equal(stopsRun, result.StopsRun);
    }

    [Fact]
    public async Task Requests_the_eod_endpoint_with_dates_and_json()
    {
        var handler = new FakeHandler((HttpStatusCode.OK, TwoDays, null));
        var provider = new EodhdPriceProvider(new HttpClient(handler), new EodhdOptions { ApiKey = "secret-key" });

        var result = await provider.GetDailyPricesAsync(new PriceSymbol("ASX", "BHP"), new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 15));

        Assert.True(result.IsOk);
        Assert.Equal(2, result.Prices.Count);
        var uri = Assert.Single(handler.Requests);
        Assert.Equal("/api/eod/BHP.AU", uri.AbsolutePath);
        Assert.Contains("api_token=secret-key", uri.Query);
        Assert.Contains("fmt=json", uri.Query);
        Assert.Contains("from=2026-10-05", uri.Query);
        Assert.Contains("to=2026-10-15", uri.Query);
    }

    [Fact]
    public async Task Retries_once_after_a_429()
    {
        var handler = new FakeHandler(
            (HttpStatusCode.TooManyRequests, "", TimeSpan.Zero),
            (HttpStatusCode.OK, TwoDays, null));
        var provider = new EodhdPriceProvider(new HttpClient(handler), new EodhdOptions { ApiKey = "k" });

        var result = await provider.GetDailyPricesAsync(new PriceSymbol("FOREX", "USD/AUD"), new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 15));

        Assert.True(result.IsOk);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("/api/eod/USDAUD.FOREX", handler.Requests[0].AbsolutePath);
    }

    [Fact]
    public async Task Gives_up_after_a_second_429()
    {
        var handler = new FakeHandler(
            (HttpStatusCode.TooManyRequests, "", TimeSpan.Zero),
            (HttpStatusCode.TooManyRequests, "", TimeSpan.Zero));
        var provider = new EodhdPriceProvider(new HttpClient(handler), new EodhdOptions { ApiKey = "k" });

        var result = await provider.GetDailyPricesAsync(new PriceSymbol("ASX", "BHP"), new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 15));

        Assert.Equal(PriceFetchStatus.RateLimited, result.Status);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Unmapped_exchange_and_missing_key_never_call_the_api()
    {
        var handler = new FakeHandler();
        var noKey = new EodhdPriceProvider(new HttpClient(handler), new EodhdOptions());
        var withKey = new EodhdPriceProvider(new HttpClient(handler), new EodhdOptions { ApiKey = "k" });
        var day = new DateOnly(2026, 10, 15);

        Assert.Equal(PriceFetchStatus.Unauthorized, (await noKey.GetDailyPricesAsync(new PriceSymbol("ASX", "BHP"), day, day)).Status);
        Assert.Equal(PriceFetchStatus.NotSupported, (await withKey.GetDailyPricesAsync(new PriceSymbol("MADEUP", "BHP"), day, day)).Status);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Network_errors_do_not_leak_the_key()
    {
        var handler = new FakeHandler { Throw = new HttpRequestException("Failed GET https://eodhd.com/api/eod/BHP.AU?api_token=secret-key") };
        var provider = new EodhdPriceProvider(new HttpClient(handler), new EodhdOptions { ApiKey = "secret-key" });

        var result = await provider.GetDailyPricesAsync(new PriceSymbol("ASX", "BHP"), new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 15));

        Assert.Equal(PriceFetchStatus.Failed, result.Status);
        Assert.DoesNotContain("secret-key", result.Message);
    }

    private sealed class FakeHandler(params (HttpStatusCode Status, string Body, TimeSpan? RetryAfter)[] responses) : HttpMessageHandler
    {
        private int next;
        public List<Uri> Requests { get; } = [];
        public Exception? Throw { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            if (Throw is not null) throw Throw;
            var (status, body, retryAfter) = responses[next++];
            var response = new HttpResponseMessage(status) { Content = new StringContent(body) };
            if (retryAfter is not null)
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(retryAfter.Value);
            return Task.FromResult(response);
        }
    }
}
