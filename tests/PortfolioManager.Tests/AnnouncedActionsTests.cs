using PortfolioManager.Core.CorporateActions;
using PortfolioManager.Core.Prices;

namespace PortfolioManager.Tests;

public class AnnouncedActionsTests
{
    private static DividendSuggestion Dividend(decimal units = 1000, decimal perUnit = 1.25m, FrankingGuess? franking = null,
        string exchange = "ASX", string? currency = "AUD", bool drp = false) =>
        new(7, exchange, "CBA", new DateOnly(2026, 8, 20), new DateOnly(2026, 9, 25), perUnit, currency, "Final", units, franking, drp, "EODHD");

    [Fact]
    public void Reads_eodhd_dividends_using_the_unadjusted_amount()
    {
        var result = EodhdPriceProvider.ParseDividends("""
            [{"date":"2026-08-20","declarationDate":"2026-08-13","recordDate":"2026-08-21","paymentDate":"2026-09-25",
              "period":"Final","value":0.625,"unadjustedValue":1.25,"currency":"aud"},
             {"date":"2026-02-12","value":1.1,"currency":"AUD"},
             {"date":"bad","value":1},
             {"date":"2026-01-01","value":0}]
            """);

        Assert.True(result.IsOk);
        Assert.Equal(2, result.Items.Count);
        Assert.Equal(new DateOnly(2026, 2, 12), result.Items[0].ExDate);
        Assert.Null(result.Items[0].PaymentDate);
        var final = result.Items[1];
        Assert.Equal(1.25m, final.AmountPerUnit);
        Assert.Equal("AUD", final.Currency);
        Assert.Equal(new DateOnly(2026, 9, 25), final.PaymentDate);
        Assert.Equal("Final", final.Period);
    }

    [Fact]
    public void An_error_object_is_a_failure()
    {
        var result = EodhdPriceProvider.ParseDividends("""{"message":"Ticker not found"}""");
        Assert.False(result.IsOk);
        Assert.Equal("Ticker not found", result.Message);
    }

    [Theory]
    [InlineData("4.000000/1.000000", 4, 1)]
    [InlineData("1.000000/10.000000", 1, 10)]
    [InlineData("1.5/1", 3, 2)]
    [InlineData("3/2", 3, 2)]
    public void Reads_split_ratios(string text, int newUnits, int oldUnits) =>
        Assert.Equal((newUnits, oldUnits), EodhdPriceProvider.SplitText(text));

    [Theory]
    [InlineData("")]
    [InlineData("1/1")]
    [InlineData("0/1")]
    [InlineData("abc")]
    public void Rejects_bad_split_ratios(string text) => Assert.Null(EodhdPriceProvider.SplitText(text));

    [Fact]
    public void Reads_eodhd_splits()
    {
        var result = EodhdPriceProvider.ParseSplits("""[{"date":"2026-03-02","split":"2.000000/1.000000"},{"date":"2026-04-01","split":"x"}]""");
        Assert.Equal([new AnnouncedSplit(new DateOnly(2026, 3, 2), 2, 1)], result.Items);
    }

    [Fact]
    public void Franking_follows_the_last_payout()
    {
        var full = AnnouncedPayouts.FromLastPayout(700, 0, 300);
        Assert.Equal(1m, full!.FrankedShare);
        Assert.Equal(Math.Round(300m / 700m, 6), full.CreditPerFrankedDollar);
        Assert.Contains("fully franked", full.Basis);

        var part = AnnouncedPayouts.FromLastPayout(50, 50, null);
        Assert.Equal(0.5m, part!.FrankedShare);
        Assert.Equal(FrankingGuess.StandardCreditRate, part.CreditPerFrankedDollar);

        Assert.Null(AnnouncedPayouts.FromLastPayout(null, null, null));
        Assert.Null(AnnouncedPayouts.FromLastPayout(0, 0, 0));
    }

    [Fact]
    public void Builds_a_payout_that_passes_the_payout_checks()
    {
        var payout = AnnouncedPayouts.ToPayout(Dividend(units: 1000, perUnit: 1.25m, franking: new FrankingGuess(1m, FrankingGuess.StandardCreditRate, "")));

        Assert.Equal(1250m, payout.Total);
        Assert.Equal(1250m, payout.Franked);
        Assert.Equal(0m, payout.Unfranked);
        Assert.Equal(535.71m, payout.FrankingCredit);
        Assert.Equal(new DateTime(2026, 9, 25), payout.Date);
        Assert.Contains("ex 20 Aug 2026", payout.Comment);
        Assert.Empty(payout.Validate());
    }

    [Fact]
    public void Part_franked_amounts_still_add_up_to_the_cent()
    {
        var payout = AnnouncedPayouts.ToPayout(Dividend(units: 333, perUnit: 0.3333m, franking: new FrankingGuess(0.3333m, FrankingGuess.StandardCreditRate, "")));
        Assert.Equal(payout.Total, payout.Franked + payout.Unfranked);
        Assert.Empty(payout.Validate());
    }

    [Fact]
    public void Units_go_back_to_the_ex_date_basis_after_a_later_split()
    {
        Assert.Equal(500m, AnnouncedPayouts.UnitsOnTheDay(1000, 2));
        Assert.Equal(1000m, AnnouncedPayouts.UnitsOnTheDay(1000, 1));
    }

    [Fact]
    public void One_click_only_when_nothing_needs_checking()
    {
        var franked = new FrankingGuess(1m, FrankingGuess.StandardCreditRate, "");
        Assert.Null(AnnouncedPayouts.NeedsReview(Dividend(franking: franked), "AUD"));
        Assert.Contains("Franking", AnnouncedPayouts.NeedsReview(Dividend(), "AUD"));
        Assert.Contains("USD", AnnouncedPayouts.NeedsReview(Dividend(exchange: "NASDAQ", currency: "USD"), "AUD"));
        Assert.Null(AnnouncedPayouts.NeedsReview(Dividend(exchange: "NZX", currency: "AUD"), "AUD"));
        Assert.Contains("DRP", AnnouncedPayouts.NeedsReview(Dividend(franking: franked, drp: true), "AUD"));
    }

    [Fact]
    public async Task Update_saves_dividends_and_splits_and_stops_on_a_bad_key()
    {
        var bhp = new PriceSymbol("ASX", "BHP");
        var cba = new PriceSymbol("ASX", "CBA");
        var store = new FakeStore([bhp, cba]);
        var provider = new FakeProvider
        {
            Dividends = { [bhp] = AnnouncementFetchResult<AnnouncedDividend>.Ok([new AnnouncedDividend(new DateOnly(2026, 9, 4), null, null, null, 0.9m, "AUD", null)]) },
            Splits = { [bhp] = AnnouncementFetchResult<AnnouncedSplit>.Ok([new AnnouncedSplit(new DateOnly(2026, 9, 1), 2, 1)]) },
        };
        provider.Dividends[cba] = AnnouncementFetchResult<AnnouncedDividend>.Fail(PriceFetchStatus.Unauthorized, "bad key");

        var summary = await new AnnouncementUpdateService(store, provider).RunAsync(new DateOnly(2026, 8, 1));

        Assert.Equal(1, summary.Dividends);
        Assert.Equal(1, summary.Splits);
        Assert.Equal(["ASX:BHP 2026-09-04", "ASX:BHP split 2026-09-01"], store.Saved);
        Assert.Single(summary.Errors);
        Assert.Equal(0, provider.SplitCallsFor(cba));
    }

    private sealed class FakeStore(IReadOnlyList<PriceSymbol> held) : IAnnouncementStore
    {
        public List<string> Saved { get; } = [];
        public Task<IReadOnlyList<PriceSymbol>> GetHeldStocksAsync(CancellationToken ct = default) => Task.FromResult(held);
        public Task SaveDividendAsync(PriceSymbol symbol, AnnouncedDividend d, string source, CancellationToken ct = default)
        {
            Saved.Add($"{symbol} {d.ExDate:yyyy-MM-dd}");
            return Task.CompletedTask;
        }
        public Task SaveSplitAsync(PriceSymbol symbol, AnnouncedSplit s, string source, CancellationToken ct = default)
        {
            Saved.Add($"{symbol} split {s.EffectiveDate:yyyy-MM-dd}");
            return Task.CompletedTask;
        }
    }

    private sealed class FakeProvider : IAnnouncementProvider
    {
        private readonly List<PriceSymbol> _splitCalls = [];
        public string Name => "Fake";
        public Dictionary<PriceSymbol, AnnouncementFetchResult<AnnouncedDividend>> Dividends { get; } = [];
        public Dictionary<PriceSymbol, AnnouncementFetchResult<AnnouncedSplit>> Splits { get; } = [];
        public int SplitCallsFor(PriceSymbol s) => _splitCalls.Count(c => c == s);

        public Task<AnnouncementFetchResult<AnnouncedDividend>> GetDividendsAsync(PriceSymbol symbol, DateOnly from, CancellationToken ct = default) =>
            Task.FromResult(Dividends.GetValueOrDefault(symbol) ?? AnnouncementFetchResult<AnnouncedDividend>.Ok([]));

        public Task<AnnouncementFetchResult<AnnouncedSplit>> GetSplitsAsync(PriceSymbol symbol, DateOnly from, CancellationToken ct = default)
        {
            _splitCalls.Add(symbol);
            return Task.FromResult(Splits.GetValueOrDefault(symbol) ?? AnnouncementFetchResult<AnnouncedSplit>.Ok([]));
        }
    }
}
