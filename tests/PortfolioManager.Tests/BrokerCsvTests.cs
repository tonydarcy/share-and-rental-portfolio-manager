using PortfolioManager.Core.Imports;

namespace PortfolioManager.Tests;

public class BrokerCsvTests
{
    private static readonly DateTime Today = new(2026, 10, 21);

    private static ShareTradeRow Only(ImportPreview<ShareTradeRow> p)
    {
        Assert.Null(p.FileError);
        Assert.All(p.Lines, l => Assert.Empty(l.Errors));
        return Assert.Single(p.ValidRows);
    }

    [Fact]
    public void CommSec_transactions_work_out_brokerage_from_the_debit()
    {
        var csv = """
            Date,Reference,Details,Debit($),Credit($),Balance($)
            02/09/2026,C1234567,B 100 CBA @ 95.500000,"9,569.95",,"10,000.00"
            03/09/2026,,Direct Credit 123 DIVIDEND,,120.00,"10,120.00"
            """;
        Assert.Equal("commsec", BrokerCsv.Detect(csv));
        var t = Only(BrokerCsv.Parse(BrokerCsv.Auto, csv, Today));

        Assert.Equal(("ASX", "CBA", "Buy", 100), (t.StockExchangeCode, t.StockCode, t.Type, t.Quantity));
        Assert.Equal(new DateTime(2026, 9, 2), t.Date);
        Assert.Equal(95.5m, t.Price);
        Assert.Equal(19.95m, t.Brokerage);
        Assert.Contains("C1234567", t.Comment);
    }

    [Fact]
    public void CommSec_sale_brokerage_comes_off_the_credit()
    {
        var csv = "Date,Reference,Details,Debit($),Credit($),Balance($)\n10/09/2026,C99,S 50 BHP @ 45.20,,2240.05,1\n";
        var t = Only(BrokerCsv.Parse("commsec", csv, Today));
        Assert.Equal(("Sell", 50, 45.2m, 19.95m), (t.Type, t.Quantity, t.Price, t.Brokerage));
    }

    [Fact]
    public void SelfWealth_style_headers_with_brokerage_and_a_title_line()
    {
        var csv = """
            Trade History for account 12345
            Trade Date,Settlement Date,Action,Reference,Code,Name,Units,Average Price,Consideration,Brokerage,Total
            2026-08-01 10:02:11,2026-08-05,Buy,SW1,VAS,Vanguard Australian Shares,10,100.00,1000.00,9.50,1009.50
            2026-08-02,2026-08-06,Sell,SW2,BHP,BHP Group,5,40,200,9.50,190.50
            """;
        var p = BrokerCsv.Parse(BrokerCsv.Auto, csv, Today);
        Assert.Equal(2, p.ValidCount);
        var buy = p.ValidRows.First();
        Assert.Equal(("ASX", "VAS", "Vanguard Australian Shares", "Buy", 10, 100m, 9.5m), (buy.StockExchangeCode, buy.StockCode, buy.Name, buy.Type, buy.Quantity, buy.Price, buy.Brokerage));
        Assert.Equal(new DateTime(2026, 8, 1), buy.Date);
        Assert.Equal("Sell", p.ValidRows.Last().Type);
    }

    [Fact]
    public void Brokerage_and_gst_columns_add_up_unless_one_includes_gst()
    {
        var separate = "Date,Code,Type,Quantity,Price,Brokerage,GST\n1/08/2026,CBA,B,10,100,10,1\n";
        Assert.Equal(11m, Only(BrokerCsv.Parse("cmc", separate, Today)).Brokerage);

        var inclusive = "Date,Code,Type,Quantity,Price,Brokerage inc GST,GST\n1/08/2026,CBA,B,10,100,11,1\n";
        Assert.Equal(11m, Only(BrokerCsv.Parse("cmc", inclusive, Today)).Brokerage);
    }

    [Fact]
    public void Stake_us_trades_keep_their_currency_and_warn_about_the_rate()
    {
        var csv = "Trade Date,Symbol,Side,Units,Effective Price (USD),Fees (USD),Currency\n2026-07-01,AAPL,BUY,3,200.50,3,USD\n";
        var p = BrokerCsv.Parse("stake", csv, Today, "AUD", "NASDAQ");
        var t = Only(p);
        Assert.Equal(("NASDAQ", "AAPL", "USD", 1m, 3m), (t.StockExchangeCode, t.StockCode, t.Currency, t.ExchangeRate, t.Brokerage));
        Assert.Contains(p.Lines[0].Warnings, w => w.Contains("USD"));
    }

    [Fact]
    public void Codes_with_an_exchange_suffix_and_dividend_lines()
    {
        var csv = "Date,Type,Ticker,Units,Price,Brokerage\n2026-06-01,Buy,DHHF.AX,20,35,0\n2026-06-02,Dividend,DHHF.AX,,,\n";
        var t = Only(BrokerCsv.Parse("pearler", csv, Today, "AUD", "NYSE"));
        Assert.Equal(("ASX", "DHHF"), (t.StockExchangeCode, t.StockCode));
    }

    [Fact]
    public void Price_is_worked_out_from_the_total_when_missing()
    {
        var csv = "Trade Date,Ticker,Transaction Type,Quantity,Brokerage,Amount\n15/05/2026,NDQ,Buy,10,2,402\n";
        Assert.Equal(40m, Only(BrokerCsv.Parse("superhero", csv, Today)).Price);
    }

    [Fact]
    public void Part_units_are_imported_not_rounded()
    {
        var csv = "Trade Date,Symbol,Side,Units,Price\n2026-07-01,TSLA,BUY,0.5,200\n";
        var line = Assert.Single(BrokerCsv.Parse("stake", csv, Today).Lines);
        Assert.Empty(line.Errors);
        Assert.Equal(0.5m, line.Value!.Quantity);

        var tooFine = "Trade Date,Symbol,Side,Units,Price\n2026-07-01,TSLA,BUY,0.123456789,200\n";
        Assert.Contains(Assert.Single(BrokerCsv.Parse("stake", tooFine, Today).Lines).Errors, e => e.Contains("decimal places"));
    }

    [Fact]
    public void Interactive_brokers_activity_statement()
    {
        var csv = """
            Statement,Header,Field Name,Field Value
            Trades,Header,DataDiscriminator,Asset Category,Currency,Symbol,Date/Time,Quantity,T. Price,C. Price,Proceeds,Comm/Fee,Basis,Realized P/L,MTM P/L,Code
            Trades,Data,Order,Stocks,USD,MSFT,"2026-03-04, 10:15:00",10,400.25,401,-4002.5,-1,4003.5,0,7.5,O
            Trades,Data,Order,Stocks,AUD,BHP,"2026-03-05, 11:00:00",-20,45,45,900,-6,-850,44,0,C
            Trades,SubTotal,,Stocks,USD,MSFT,,10,,,-4002.5,-1,4003.5,0,7.5,
            Trades,Data,Order,Equity and Index Options,USD,SPY 240621C,"2026-03-05, 11:00:00",1,5,5,-500,-1,501,0,0,O
            """;
        Assert.Equal("ibkr", BrokerCsv.Detect(csv));
        var p = BrokerCsv.Parse(BrokerCsv.Auto, csv, Today, "AUD", "NASDAQ");
        Assert.Equal(2, p.ValidCount);
        var msft = p.ValidRows.First();
        Assert.Equal(("NASDAQ", "MSFT", "Buy", 10, 400.25m, 1m, "USD"), (msft.StockExchangeCode, msft.StockCode, msft.Type, msft.Quantity, msft.Price, msft.Brokerage, msft.Currency));
        var bhp = p.ValidRows.Last();
        Assert.Equal(("ASX", "Sell", 20, 6m), (bhp.StockExchangeCode, bhp.Type, bhp.Quantity, bhp.Brokerage));
    }

    [Fact]
    public void The_standard_file_still_works_through_auto()
    {
        var p = BrokerCsv.Parse(BrokerCsv.Auto, "ASX,ANZ,ANZ Group,Buy,2024-02-29,147,28.345714,2,AUD,1,x\n", Today);
        Assert.Equal(147, Only(p).Quantity);
    }

    [Fact]
    public void A_file_without_the_needed_columns_says_so()
    {
        var p = BrokerCsv.Parse("headers", "Foo,Bar\n1,2\n", Today);
        Assert.Contains("header", p.FileError);
    }

    [Theory]
    [InlineData("1,234.50", 1234.50)]
    [InlineData("$95.50", 95.50)]
    [InlineData("(12.00)", -12)]
    [InlineData("A$12", 12)]
    [InlineData("-3", -3)]
    public void Reads_amounts(string text, double expected)
    {
        Assert.True(BrokerCsv.TryAmount(text, out var v));
        Assert.Equal((decimal)expected, v);
    }
}

public class FxRateTests
{
    [Fact]
    public void A_usd_aud_close_is_turned_into_usd_per_aud()
    {
        var r = PortfolioManager.Core.Prices.FxRateService.FromPrice("USD", "AUD", "USD/AUD", new DateOnly(2026, 9, 1), 1.5m);
        Assert.Equal(0.666667m, r!.Rate);
        Assert.Equal(1500m, r.ToPortfolio(1000));
    }

    [Fact]
    public void An_aud_usd_close_is_used_as_it_is()
    {
        var r = PortfolioManager.Core.Prices.FxRateService.FromPrice("USD", "AUD", "AUD/USD", new DateOnly(2026, 9, 1), 0.65m);
        Assert.Equal(0.65m, r!.Rate);
        Assert.Equal(1538.46m, r.ToPortfolio(1000));
        Assert.Null(PortfolioManager.Core.Prices.FxRateService.FromPrice("USD", "AUD", "GBP/AUD", new DateOnly(2026, 9, 1), 2m));
    }

    [Theory]
    [InlineData(1, 0.65, true)]
    [InlineData(1.54, 0.65, true)]
    [InlineData(0.66, 0.65, false)]
    public void Flags_rates_far_from_the_market(double entered, double market, bool wrong) =>
        Assert.Equal(wrong, PortfolioManager.Core.Prices.FxRateService.LooksWrong((decimal)entered, (decimal)market));

    [Fact]
    public void Broker_exchange_names_use_the_site_codes()
    {
        var csv = "Date,Code,Exchange,Type,Quantity,Price\n2026-07-01,MSFT,NMS,Buy,1,400\n2026-07-01,VOD,XLON,Buy,1,1\n";
        var rows = BrokerCsv.Parse("headers", csv, new DateTime(2026, 10, 21)).ValidRows.ToList();
        Assert.Equal(["NASDAQ", "LSE"], rows.Select(r => r.StockExchangeCode));
    }
}
