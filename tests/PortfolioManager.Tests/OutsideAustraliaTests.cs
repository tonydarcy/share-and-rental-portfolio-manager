using PortfolioManager.Core.Prices;
using PortfolioManager.Core.Tax;
using PortfolioManager.Core.Trades;

namespace PortfolioManager.Tests;

public class OutsideAustraliaTests
{
    [Fact]
    public void London_symbol_list_gives_the_codes_priced_in_pence()
    {
        var codes = EodhdPriceProvider.ParsePenceCodes("""
            [{"Code":"VOD","Currency":"GBX"},{"Code":"VUSA","Currency":"GBP"},{"Code":"BP","Currency":"GBp"},{"Code":"CSPX","Currency":"USD"}]
            """);
        Assert.Equal(["BP", "VOD"], codes.Order());
        Assert.Empty(EodhdPriceProvider.ParsePenceCodes("""{"message":"bad key"}"""));
        Assert.Empty(EodhdPriceProvider.ParsePenceCodes("not json"));
    }

    [Theory]
    [InlineData("GBX", true)]
    [InlineData("gbx", true)]
    [InlineData("GBp", true)]
    [InlineData("GBP", false)]
    [InlineData(null, false)]
    public void Pence_currency_codes(string? currency, bool pence) => Assert.Equal(pence, EodhdPriceProvider.IsPence(currency));

    [Fact]
    public void Pence_prices_become_pounds()
    {
        var day = new DateOnly(2026, 11, 3);
        var result = EodhdPriceProvider.InPounds(PriceFetchResult.Ok([new DailyPrice(day, 7250, 7300, null, 7280, 1000)]));
        var p = Assert.Single(result.Prices);
        Assert.Equal(72.8, p.Close, 6);
        Assert.Equal(72.5, p.Open!.Value, 6);
        Assert.Null(p.Low);
        Assert.Equal(1000, p.Volume);
    }

    [Fact]
    public void Pence_dividends_become_pounds()
    {
        var result = EodhdPriceProvider.ParseDividends("""[{"date":"2026-06-05","value":4.5,"currency":"GBX"}]""");
        var d = Assert.Single(result.Items);
        Assert.Equal(0.045m, d.AmountPerUnit);
        Assert.Equal("GBP", d.Currency);
    }

    private static CgtSummary Summary(decimal longTerm, decimal shortTerm, decimal lossesOnShort, decimal lossesOnLong, decimal net) =>
        new(CgtDiscountRate.None, longTerm, shortTerm, 0, 0, lossesOnShort, lossesOnLong, 0, net, 0);

    [Fact]
    public void Uk_exempt_amount_comes_off_the_net_gain()
    {
        var a = CountryCgt.Allowance("GB", "Individual", Summary(5000, 0, 0, 0, 5000))!;
        Assert.Equal(-3000m, a.Amount);
        Assert.Equal(2000m, a.Result);
        Assert.Equal(-1000m, CountryCgt.Allowance("GB", "Individual", Summary(1000, 0, 0, 0, 1000))!.Amount);
        Assert.Equal(-1500m, CountryCgt.Allowance("GB", "Family trust", Summary(5000, 0, 0, 0, 5000))!.Amount);
        Assert.Null(CountryCgt.Allowance("GB", "Company", Summary(5000, 0, 0, 0, 5000)));
    }

    [Fact]
    public void Canada_includes_half_the_gain()
    {
        var a = CountryCgt.Allowance("CA", "Individual", Summary(0, 1001, 0, 0, 1001))!;
        Assert.Equal(500.50m, a.Result);
        Assert.Equal(-500.50m, a.Amount);
    }

    [Fact]
    public void India_exempts_long_term_gains_up_to_the_limit()
    {
        // 200,000 long term less 20,000 losses = 180,000; 125,000 of it exempt
        var a = CountryCgt.Allowance("IN", "Individual", Summary(200_000, 10_000, 0, 20_000, 190_000))!;
        Assert.Equal(-125_000m, a.Amount);
        Assert.Equal(65_000m, a.Result);
        Assert.Null(CountryCgt.Allowance("AU", "Individual", Summary(1, 0, 0, 0, 1)));
        Assert.Null(CountryCgt.Allowance("US", "Individual", Summary(1, 0, 0, 0, 1)));
    }

    [Fact]
    public void Loss_with_a_buy_back_inside_30_days_is_flagged()
    {
        var sold = new DateOnly(2026, 3, 10);
        CgtEvent Sale(string code, decimal gain, int buyId) => new("NYSE", code, new DateOnly(2025, 1, 2), sold, 10, 1000, 1000 + gain, BuyTradeId: buyId);
        TradeLot Buy(int id, string code, DateOnly date) => new(id, "NYSE", code, "Buy", date, 5, 90);
        var warnings = CountryCgt.Repurchases(
            [Sale("AAA", -200, 1), Sale("BBB", -50, 2), Sale("CCC", 300, 3), Sale("DDD", -80, 4)],
            [Buy(1, "AAA", new(2025, 1, 2)), Buy(10, "AAA", sold.AddDays(30)), Buy(2, "BBB", new(2025, 1, 2)), Buy(11, "BBB", sold.AddDays(31)),
             Buy(12, "CCC", sold.AddDays(1)), Buy(4, "DDD", sold.AddDays(-5))]);
        var w = Assert.Single(warnings);
        Assert.Equal("AAA", w.StockCode);
        Assert.Equal(200m, w.Loss);
        Assert.Equal(sold.AddDays(30), w.Bought);
        Assert.Equal("wash sale", CountryCgt.RepurchaseRuleName("US"));
        Assert.Null(CountryCgt.RepurchaseRuleName("GB"));
    }

    private static TradeForm IndianBuy(decimal brokerage, decimal? stt) => new()
    {
        Date = new DateOnly(2026, 10, 14), Type = "Buy", Market = "NSE", Code = "INFY", Quantity = 10, Price = 1500m,
        Brokerage = brokerage, SecuritiesTax = stt, Currency = "INR", ExchangeRate = 1m,
    };

    [Fact]
    public void Stt_is_part_of_the_brokerage()
    {
        var today = new DateOnly(2026, 11, 4);
        Assert.Equal(15m, TradeEntry.Validate(IndianBuy(40, 15), today).Value!.SecuritiesTax);
        Assert.Equal(0m, TradeEntry.Validate(IndianBuy(40, null), today).Value!.SecuritiesTax);
        Assert.Contains(TradeEntry.Validate(IndianBuy(10, 15), today).Errors, e => e.Field == nameof(TradeForm.SecuritiesTax));
        Assert.Contains(TradeEntry.Validate(IndianBuy(10, -1), today).Errors, e => e.Field == nameof(TradeForm.SecuritiesTax));
    }

    [Fact]
    public void Stt_comes_out_of_the_cost_and_back_into_the_proceeds()
    {
        TradeLot Lot(int id, string type) => new(id, "NSE", "INFY", type, new DateOnly(2026, 1, 1), 10, 1504m);
        var lots = CgtDiscountService.WithoutSecuritiesTax([Lot(1, "Buy"), Lot(2, "Sell"), Lot(3, "Buy")],
            new Dictionary<int, decimal> { [1] = 15m, [2] = 20m });
        Assert.Equal(1502.5m, lots[0].CostBasisPerUnit);
        Assert.Equal(1506m, lots[1].CostBasisPerUnit);
        Assert.Equal(1504m, lots[2].CostBasisPerUnit);
    }
}
