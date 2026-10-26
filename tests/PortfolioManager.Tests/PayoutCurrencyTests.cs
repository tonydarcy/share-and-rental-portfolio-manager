using PortfolioManager.Core.Payouts;

namespace PortfolioManager.Tests;

public class PayoutCurrencyTests
{
    // A US dividend: $100.00 less 15% withholding, so $85.00 arrives
    private static PayoutInput UsDividend() => new()
    {
        StockExchangeCode = "NASDAQ", StockCode = "AAPL", Date = new DateTime(2026, 5, 15),
        Unfranked = 100m, ForeignTax = 15m, Total = 85m, Currency = "usd", ExchangeRate = 0.65m,
    };

    private static Dictionary<string, object?> Values(object args) => args.GetType().GetProperties().ToDictionary(p => p.Name, p => p.GetValue(args));

    [Fact]
    public void Converts_each_amount_and_keeps_the_parts_adding_up()
    {
        var aud = UsDividend().ToPortfolioCurrency(0.65m);
        Assert.Equal(130.77m, aud.Total);        // 85 / 0.65 = 130.769
        Assert.Equal(23.08m, aud.ForeignTax);    // 15 / 0.65 = 23.077
        Assert.Equal(153.85m, aud.Unfranked);    // 100 / 0.65 = 153.846, the largest part takes up the rounding
        Assert.Equal(aud.Total, aud.PartsTotal);
        Assert.Empty(aud.ValidateAmounts());
    }

    [Fact]
    public void Proc_parameters_are_in_the_portfolio_currency_and_keep_what_was_entered()
    {
        var v = Values(PayoutService.AddParameters(3, UsDividend(), "AUD"));
        Assert.Equal((130.77d, 153.85d, 23.08d), ((double)v["dblTotal"]!, (double)v["dblUnFrankedAmount"]!, (double)v["dblForeignIncomeTaxAmount"]!));
        Assert.Equal(("USD", 0.65d, 85d), ((string)v["strCurrency"]!, (double?)v["dblExchangeRate"], (double?)v["dblForeignTotal"]));
    }

    [Fact]
    public void A_payout_in_the_portfolio_currency_is_unchanged()
    {
        var p = UsDividend();
        p.Currency = "AUD";
        var v = Values(PayoutService.AddParameters(3, p, "AUD"));
        Assert.Equal(85d, (double)v["dblTotal"]!);
        Assert.Null(v["strCurrency"]);
        Assert.Null(v["dblExchangeRate"]);
    }

    [Fact]
    public void Checks_the_currency_and_rate()
    {
        var p = UsDividend();
        Assert.Empty(p.ValidateCurrency("AUD"));
        p.ExchangeRate = 0;
        Assert.NotEmpty(p.ValidateCurrency("AUD"));
        p.Currency = "US$";
        Assert.NotEmpty(p.ValidateCurrency("AUD"));
        var noRate = UsDividend();
        noRate.ExchangeRate = null;
        Assert.Throws<ArgumentException>(() => PayoutService.AddParameters(3, noRate, "AUD"));
    }
}
