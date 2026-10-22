using PortfolioManager.Core.Prices;

namespace PortfolioManager.Tests;

public class PriceUpdateServiceTests
{
    private static readonly DateOnly From = new(2026, 10, 5), To = new(2026, 10, 15);
    private static readonly PriceSymbol Bhp = new("ASX", "BHP"), Cba = new("ASX", "CBA"), Aapl = new("NASDAQ", "AAPL"), UsdAud = PriceSymbol.Forex("USD", "AUD");

    private static DailyPrice Day(int day, double close) => new(new DateOnly(2026, 10, day), close, close, close, close, 1000);

    [Fact]
    public void Forex_pairs_cover_each_foreign_currency_once()
    {
        var pairs = PriceStore.ForexSymbols([("USD", "AUD"), ("usd ", "AUD"), ("AUD", "AUD"), ("GBP", "AUD"), ("", "AUD"), ("USD-1", "AUD"), ("AUD", "USD")]);

        Assert.Equal(["FOREX:AUD/USD", "FOREX:GBP/AUD", "FOREX:USD/AUD"], pairs.Select(p => p.ToString()));
    }

    [Fact]
    public async Task Saves_prices_oldest_first_then_updates_trends_per_exchange()
    {
        var store = new FakeStore(Bhp, Aapl);
        var provider = new FakeProvider { [Bhp] = PriceFetchResult.Ok([Day(14, 45.6), Day(13, 45.0)]), [Aapl] = PriceFetchResult.Ok([Day(14, 250)]) };

        var summary = await new PriceUpdateService(store, provider).RunAsync(From, To);

        Assert.False(summary.HasErrors);
        Assert.Equal(3, summary.PricesSaved);
        Assert.Equal(["ASX:BHP 13", "ASX:BHP 14", "NASDAQ:AAPL 14"], store.Uploads);
        Assert.Equal(["ASX", "NASDAQ"], store.TrendExchanges);
    }

    [Fact]
    public async Task A_failing_symbol_does_not_stop_the_others()
    {
        var store = new FakeStore(Bhp, Cba, Aapl);
        var provider = new FakeProvider
        {
            [Bhp] = PriceFetchResult.Fail(PriceFetchStatus.NotFound, "Ticker not found"),
            [Cba] = PriceFetchResult.Fail(PriceFetchStatus.RateLimited, "Too many requests"),
            [Aapl] = PriceFetchResult.Ok([Day(14, 250)]),
        };

        var summary = await new PriceUpdateService(store, provider).RunAsync(From, To);

        Assert.Equal(1, summary.Succeeded);
        Assert.Equal(2, summary.Failed);
        Assert.Equal(["NASDAQ:AAPL 14"], store.Uploads);
        Assert.Equal(["NASDAQ"], store.TrendExchanges);
    }

    [Fact]
    public async Task Used_up_allowance_skips_the_rest_without_calling_the_api()
    {
        var store = new FakeStore(Bhp, Cba, Aapl);
        var provider = new FakeProvider
        {
            [Bhp] = PriceFetchResult.Ok([Day(14, 45.6)]),
            [Cba] = PriceFetchResult.Fail(PriceFetchStatus.QuotaExceeded, "Daily API limit reached"),
        };

        var summary = await new PriceUpdateService(store, provider).RunAsync(From, To);

        Assert.Equal([Bhp, Cba], provider.Calls);
        Assert.StartsWith("Skipped", summary.Symbols.Single(s => s.Symbol == Aapl).Message);
        Assert.Equal(["ASX"], store.TrendExchanges);
    }

    [Fact]
    public async Task A_database_error_on_one_symbol_is_recorded_and_the_run_goes_on()
    {
        var store = new FakeStore(Bhp, Cba) { FailOn = Bhp };
        var provider = new FakeProvider { [Bhp] = PriceFetchResult.Ok([Day(14, 45.6)]), [Cba] = PriceFetchResult.Ok([Day(14, 120)]) };

        var summary = await new PriceUpdateService(store, provider).RunAsync(From, To);

        Assert.Contains("failed", summary.Symbols.Single(s => s.Symbol == Bhp).Message);
        Assert.True(summary.Symbols.Single(s => s.Symbol == Cba).Succeeded);
    }

    [Fact]
    public async Task No_prices_for_a_held_stock_counts_as_not_updated()
    {
        var store = new FakeStore(Bhp);
        var provider = new FakeProvider { [Bhp] = PriceFetchResult.Ok([]) };

        var summary = await new PriceUpdateService(store, provider).RunAsync(From, To);

        Assert.Equal(1, summary.Failed);
        Assert.Empty(store.TrendExchanges);
    }

    [Fact]
    public async Task Latest_fx_rate_is_saved_again_after_the_trend_clean_up()
    {
        var store = new FakeStore(UsdAud);
        var provider = new FakeProvider { [UsdAud] = PriceFetchResult.Ok([Day(13, 1.52), Day(14, 1.53)]) };

        await new PriceUpdateService(store, provider).RunAsync(From, To);

        Assert.Equal(["FOREX:USD/AUD 13", "FOREX:USD/AUD 14", "trends FOREX", "FOREX:USD/AUD 14"], store.Log);
    }

    [Fact]
    public void Always_update_symbols_are_read_from_settings()
    {
        var symbols = PriceStore.ParseSymbols(["asx:stw", " ASX : A200 ", "STW", "ASX:", "ASX:STW"]);
        Assert.Equal([new PriceSymbol("ASX", "STW"), new PriceSymbol("ASX", "A200")], symbols);
    }

    private sealed class FakeStore(params PriceSymbol[] symbols) : IPriceStore
    {
        public PriceSymbol? FailOn { get; init; }
        public List<string> Log { get; } = [];
        public List<string> Uploads { get; } = [];
        public List<string> TrendExchanges { get; } = [];

        public Task<IReadOnlyList<PriceSymbol>> GetSymbolsToUpdateAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<PriceSymbol>>(symbols);

        public Task UploadAsync(PriceSymbol symbol, DailyPrice price, CancellationToken ct = default)
        {
            if (symbol == FailOn) throw new InvalidOperationException("timeout");
            var entry = $"{symbol} {price.Date.Day}";
            Uploads.Add(entry);
            Log.Add(entry);
            return Task.CompletedTask;
        }

        public Task<int> UpdateTrendsAsync(string exchangeCode, CancellationToken ct = default)
        {
            TrendExchanges.Add(exchangeCode);
            Log.Add($"trends {exchangeCode}");
            return Task.FromResult(1);
        }
    }

    private sealed class FakeProvider : Dictionary<PriceSymbol, PriceFetchResult>, IPriceProvider
    {
        public List<PriceSymbol> Calls { get; } = [];
        public string Name => "Fake";

        public Task<PriceFetchResult> GetDailyPricesAsync(PriceSymbol symbol, DateOnly from, DateOnly to, CancellationToken ct = default)
        {
            Calls.Add(symbol);
            return Task.FromResult(TryGetValue(symbol, out var r) ? r : PriceFetchResult.Fail(PriceFetchStatus.NotFound, "none"));
        }
    }
}
