using PortfolioManager.Core.Import;

namespace PortfolioManager.Tests;

public class CsvTests
{
    [Fact]
    public void Handles_quotes_embedded_commas_line_breaks_bom_and_blank_lines()
    {
        var rows = Csv.Parse("﻿a,b,c\r\n\"x, y\",\"say \"\"hi\"\"\",\"two\nlines\"\r\n\r\n1,2,3");

        Assert.Equal(3, rows.Count);
        Assert.Equal(["a", "b", "c"], rows[0].Fields);
        Assert.Equal(["x, y", "say \"hi\"", "two\nlines"], rows[1].Fields);
        Assert.Equal(2, rows[1].Line);
        Assert.Equal(5, rows[2].Line);
    }
}

public class NavexaExchangeTests
{
    [Theory]
    [InlineData("AXW", "ASX", "AUD")]
    [InlineData("asx", "ASX", "AUD")]
    [InlineData("Australian Securities Exchange", "ASX", "AUD")]
    [InlineData("NAS", "NASDAQ", "USD")]
    [InlineData("NYQ", "NYSE", "USD")]
    [InlineData("NZE", "NZX", "NZD")]
    public void Maps_navexa_names_to_site_codes(string navexa, string code, string currency)
    {
        var match = NavexaExchanges.Map(navexa);
        Assert.True(match.Recognised);
        Assert.Equal(code, match.Code);
        Assert.Equal(currency, match.Currency);
    }

    [Fact]
    public void Unknown_exchange_is_kept_but_flagged()
    {
        var match = NavexaExchanges.Map(" xetra ");
        Assert.False(match.Recognised);
        Assert.Equal("XETRA", match.Code);
        Assert.Null(match.Currency);
    }

    [Theory]
    [InlineData("CBA.AX", "ASX", "CBA")]
    [InlineData("ASX:CBA", "ASX", "CBA")]
    [InlineData("BRK.B", null, "BRK.B")]
    [InlineData("VAS", null, "VAS")]
    public void Splits_exchange_out_of_symbols(string symbol, string? exchange, string code)
    {
        Assert.Equal((exchange, code), NavexaExchanges.SplitSymbol(symbol));
    }
}

public class NavexaValueTests
{
    [Theory]
    [InlineData("1,234.50", 1234.50)]
    [InlineData("$12.00", 12.00)]
    [InlineData("A$ 3", 3)]
    [InlineData("(4.10)", -4.10)]
    [InlineData("USD 5.5", 5.5)]
    [InlineData("-7", -7)]
    public void Parses_amounts(string text, double expected)
    {
        Assert.Null(NavexaParser.Number(text, out var value));
        Assert.Equal((decimal)expected, value);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("1.2.3")]
    [InlineData("")]
    public void Rejects_non_numbers(string text) => Assert.NotNull(NavexaParser.Number(text, out _));

    [Theory]
    [InlineData("15/10/2025")]
    [InlineData("2025-10-15")]
    [InlineData("15-Oct-2025")]
    [InlineData("15 Oct 2025")]
    [InlineData("2025-10-15T00:00:00")]
    public void Parses_australian_and_iso_dates(string text)
    {
        Assert.True(NavexaParser.TryDate(text, out var date));
        Assert.Equal(new DateTime(2025, 10, 15), date);
    }
}

public class NavexaTradeParseTests
{
    private static readonly NavexaOptions Aud = new("AUD", Today: new DateTime(2026, 10, 15));

    // Title row above the header, columns out of order, mixed-case variant headings
    private const string Sample = """
        Navexa trades export,,,,,,,,,
        trade date,CODE,Market,Trade Type,Units,Price,Brokerage,Currency,Exchange Rate,Notes
        03/02/2025,CBA,AXW,Buy,100,120.50,9.95,AUD,1,first parcel
        2025-06-30,CBA.AX,,Sell,-40,150,9.95,,,
        10/03/2025,AAPL,NAS,Buy,10,200,5,USD,0.65,
        12/03/2025,BHP,ASX,Split,100,,,AUD,,
        14/03/2025,BHP,ASX,Dividend Reinvestment,12,45.10,,AUD,,
        15/03/2025,WES,ASX,Opening Balance,50,60,0,AUD,,
        16/03/2025,VAS,ASX,Buy,10.123456789,100,0,AUD,,
        31/02/2025,VAS,ASX,Buy,10,100,0,AUD,,
        01/01/2027,VAS,ASX,Buy,10,100,0,AUD,,
        17/03/2025,MSFT,NAS,Buy,5,400,0,USD,,
        18/03/2025,,ASX,Buy,5,1,0,AUD,,
        """;

    private static NavexaTrade Line(NavexaParseResult<NavexaTrade> r, int line) => r.Rows.Single(t => t.Line == line);

    [Fact]
    public void Reads_buys_and_sells_whatever_the_column_order()
    {
        var result = NavexaParser.ParseTrades(Sample, Aud);
        Assert.Empty(result.Errors);

        var buy = Line(result, 3);
        Assert.Equal(ImportRowStatus.Ready, buy.Status);
        Assert.Equal(("Buy", "ASX", "CBA", 100, 120.50m, 9.95m, "AUD", 1m),
            (buy.Type, buy.Exchange, buy.Code, buy.Quantity, buy.Price, buy.Brokerage, buy.Currency, buy.ExchangeRate));
        Assert.Equal(new DateTime(2025, 2, 3), buy.Date);
        Assert.Equal("Imported from Navexa: first parcel", buy.Comment);
        Assert.Equal(12059.95m, buy.PortfolioTotal);

        var sell = Line(result, 4);
        Assert.Equal(ImportRowStatus.Ready, sell.Status);
        Assert.Equal(("Sell", "ASX", "CBA", 40, "AUD"), (sell.Type, sell.Exchange, sell.Code, sell.Quantity, sell.Currency));
        Assert.Equal(5990.05m, sell.PortfolioTotal);
    }

    [Fact]
    public void Turns_navexa_rates_round_to_how_the_site_stores_them()
    {
        // Navexa: 1 USD = 0.65 AUD would be odd, but the point is the direction: the site stores USD per AUD
        var usd = Line(NavexaParser.ParseTrades(Sample, Aud), 5);
        Assert.Equal(ImportRowStatus.Ready, usd.Status);
        Assert.Equal("NASDAQ", usd.Exchange);
        Assert.Equal(Math.Round(1m / 0.65m, 10), usd.ExchangeRate);
        Assert.Equal(1303.25m, usd.PortfolioTotal); // (10 x 200 + 5) x 0.65

        var asStored = Line(NavexaParser.ParseTrades(Sample, Aud with { RatesAreForeignPerPortfolioUnit = true }), 5);
        Assert.Equal(0.65m, asStored.ExchangeRate);
        Assert.Equal(3084.62m, asStored.PortfolioTotal); // 2005 / 0.65
    }

    [Fact]
    public void Lists_unsupported_types_instead_of_dropping_them()
    {
        var result = NavexaParser.ParseTrades(Sample, Aud);

        var split = Line(result, 6);
        Assert.Equal(ImportRowStatus.Unsupported, split.Status);
        Assert.Contains("Split", split.Unsupported);

        var drp = Line(result, 7);
        Assert.Equal(ImportRowStatus.Unsupported, drp.Status);
        Assert.Contains("Dividend reinvestment", drp.Unsupported);
    }

    [Fact]
    public void Opening_balance_becomes_a_buy_with_a_warning()
    {
        var row = Line(NavexaParser.ParseTrades(Sample, Aud), 8);
        Assert.Equal(ImportRowStatus.Ready, row.Status);
        Assert.Equal("Buy", row.Type);
        Assert.Single(row.Warnings);
    }

    [Theory]
    [InlineData(9, "decimal places")]
    [InlineData(10, "isn't a date")]
    [InlineData(11, "future")]
    [InlineData(12, "No exchange rate")]
    [InlineData(13, "No code")]
    public void Reports_row_errors(int line, string message)
    {
        var row = Line(NavexaParser.ParseTrades(Sample, Aud), line);
        Assert.Equal(ImportRowStatus.Error, row.Status);
        Assert.Contains(row.Errors, e => e.Contains(message));
    }

    [Fact]
    public void Missing_required_columns_is_a_file_error()
    {
        var result = NavexaParser.ParseTrades("Date,Symbol,Quantity\n01/01/2025,CBA,10", Aud);
        Assert.Empty(result.Rows);
        Assert.Contains("header", Assert.Single(result.Errors));
    }

    [Fact]
    public void Without_an_exchange_column_assumes_asx()
    {
        var result = NavexaParser.ParseTrades("Date,Symbol,Type,Quantity,Price\n01/01/2025,CBA,Buy,10,100", Aud);
        Assert.Single(result.Warnings);
        var row = Assert.Single(result.Rows);
        Assert.Equal(("ASX", "AUD", ImportRowStatus.Ready), (row.Exchange, row.Currency, row.Status));
        Assert.Equal(0m, row.Brokerage);
    }

    [Fact]
    public void Converts_brokerage_paid_in_the_portfolio_currency()
    {
        var csv = "Date,Symbol,Exchange,Type,Quantity,Price,Brokerage,Brokerage Currency,Currency,Exchange Rate\n" +
                  "01/01/2025,AAPL,NAS,Buy,10,200,15,AUD,USD,1.5";
        var row = Assert.Single(NavexaParser.ParseTrades(csv, Aud).Rows);
        Assert.Equal(ImportRowStatus.Ready, row.Status);
        Assert.Equal(10m, row.Brokerage); // 15 AUD at 1 USD = 1.5 AUD
    }

    [Fact]
    public void Flags_rows_repeated_within_the_file()
    {
        var csv = "Date,Symbol,Type,Quantity,Price\n01/01/2025,CBA,Buy,10,100\n01/01/2025,CBA,Buy,10,100";
        var rows = NavexaParser.ParseTrades(csv, Aud).Rows;
        Assert.DoesNotContain(rows[0].Warnings, w => w.Contains("Same as line"));
        Assert.Contains(rows[1].Warnings, w => w.Contains("Same as line 2"));
        Assert.All(rows, r => Assert.Equal(ImportRowStatus.Ready, r.Status));
    }
}

public class NavexaIncomeParseTests
{
    private static readonly NavexaOptions Aud = new("AUD", Today: new DateTime(2026, 10, 15));

    private const string Sample = """
        Ex Date,Payment Date,Symbol,Exchange,Franked Amount,Unfranked Amount,Franking Credits,Withholding Tax,Net Amount,Currency,Exchange Rate,Type,DRP
        20/02/2025,27/03/2025,CBA,AXW,"$1,000.00",0,428.57,,,AUD,,Dividend,No
        10/02/2025,13/02/2025,AAPL,NAS,0,25.00,0,3.75,,USD,1.6,Dividend,No
        01/03/2025,10/03/2025,VAS,ASX,30,70,12.86,,95,AUD,,Distribution,Yes
        01/03/2025,10/03/2025,XYZ,ASX,10,0,0,,,AUD,,Return of Capital,
        01/03/2025,11/03/2025,ABC,ASX,-10,0,0,,,AUD,,Dividend,
        """;

    private static NavexaPayout Line(NavexaParseResult<NavexaPayout> r, int line) => r.Rows.Single(p => p.Line == line);

    [Fact]
    public void Reads_a_franked_dividend_and_uses_the_payment_date()
    {
        var p = Line(NavexaParser.ParseIncome(Sample, Aud), 2);
        Assert.Equal(ImportRowStatus.Ready, p.Status);
        Assert.Equal(new DateTime(2025, 3, 27), p.Date);
        Assert.Equal(("ASX", "CBA", 1000m, 0m, 428.57m, 1000m), (p.Exchange, p.Code, p.Franked, p.Unfranked, p.FrankingCredit, p.Net));
        Assert.Empty(p.Warnings);
    }

    [Fact]
    public void Converts_foreign_income_and_takes_withholding_off_the_cash()
    {
        var p = Line(NavexaParser.ParseIncome(Sample, Aud), 3);
        Assert.Equal(ImportRowStatus.Ready, p.Status);
        Assert.Equal(40m, p.Unfranked);   // 25 USD x 1.6
        Assert.Equal(6m, p.ForeignTax);   // 3.75 x 1.6
        Assert.Equal(34m, p.Net);
        Assert.Equal(0m, p.TfnWithheld);
    }

    [Fact]
    public void Warns_when_net_disagrees_and_about_drp_units()
    {
        var p = Line(NavexaParser.ParseIncome(Sample, Aud), 4);
        Assert.Equal(ImportRowStatus.Ready, p.Status);
        Assert.Equal(95m, p.Net);
        Assert.Contains(p.Warnings, w => w.Contains("Net amount"));
        Assert.Contains(p.Warnings, w => w.Contains("DRP"));
    }

    [Fact]
    public void Lists_unsupported_income_and_rejects_negative_amounts()
    {
        var result = NavexaParser.ParseIncome(Sample, Aud);
        Assert.Equal(ImportRowStatus.Unsupported, Line(result, 5).Status);
        Assert.Equal(ImportRowStatus.Error, Line(result, 6).Status);
    }

    [Fact]
    public void A_gross_amount_alone_is_treated_as_unfranked()
    {
        var p = Assert.Single(NavexaParser.ParseIncome("Date,Code,Amount\n01/07/2025,VAS,123.45", Aud).Rows);
        Assert.Equal(ImportRowStatus.Ready, p.Status);
        Assert.Equal((0m, 123.45m, 123.45m), (p.Franked, p.Unfranked, p.Net));
    }

    [Fact]
    public void Needs_an_amount_column()
    {
        var result = NavexaParser.ParseIncome("Date,Code,Exchange\n01/07/2025,VAS,ASX", Aud);
        Assert.Contains("amount", Assert.Single(result.Errors));
    }
}

public class NavexaDuplicateTests
{
    private static readonly NavexaOptions Aud = new("AUD", Today: new DateTime(2026, 10, 15));

    [Fact]
    public void Skips_trades_already_in_the_portfolio_and_warns_about_near_misses()
    {
        var csv = "Date,Symbol,Exchange,Type,Quantity,Price\n" +
                  "01/02/2025,CBA,ASX,Sell,40,150\n" +
                  "02/02/2025,BHP,ASX,Buy,10,45\n" +
                  "03/02/2025,WES,ASX,Buy,10,60";
        var rows = NavexaParser.ParseTrades(csv, Aud).Rows;

        NavexaDuplicates.MarkTrades(rows,
        [
            // Old Sharesight uploads stored sells with negative units and upper-case types
            new("ASX ", "cba", "SELL", new DateTime(2025, 2, 1), -40m, 150.00001m),
            new("ASX", "BHP", "Buy", new DateTime(2025, 2, 2), 20m, 45m),
        ]);

        Assert.Equal(ImportRowStatus.Duplicate, rows[0].Status);
        Assert.Equal(ImportRowStatus.Ready, rows[1].Status);
        Assert.Contains(rows[1].Warnings, w => w.Contains("already has"));
        Assert.Equal(ImportRowStatus.Ready, rows[2].Status);
        Assert.Empty(rows[2].Warnings);
    }

    [Fact]
    public void Skips_payouts_already_in_the_portfolio()
    {
        var csv = "Payment Date,Symbol,Exchange,Franked,Unfranked\n" +
                  "27/03/2025,CBA,ASX,1000,0\n" +
                  "27/03/2025,WBC,ASX,500,0";
        var rows = NavexaParser.ParseIncome(csv, Aud).Rows;

        NavexaDuplicates.MarkPayouts(rows,
        [
            new("ASX", "CBA", new DateTime(2025, 3, 27), 1000.004m),
            new("ASX", "WBC", new DateTime(2025, 3, 27), 250m),
        ]);

        Assert.Equal(ImportRowStatus.Duplicate, rows[0].Status);
        Assert.Equal(ImportRowStatus.Ready, rows[1].Status);
        Assert.Contains(rows[1].Warnings, w => w.Contains("already has"));
    }

    [Fact]
    public void Rows_with_errors_are_not_marked_duplicate()
    {
        var rows = NavexaParser.ParseTrades("Date,Symbol,Type,Quantity,Price\nnot a date,CBA,Buy,10,100", Aud).Rows;
        NavexaDuplicates.MarkTrades(rows, [new("ASX", "CBA", "Buy", default, 10m, 100m)]);
        Assert.Equal(ImportRowStatus.Error, rows[0].Status);
        Assert.Null(rows[0].DuplicateOf);
    }
}

public class NavexaUnsupportedTests
{
    [Fact]
    public void An_unsupported_row_is_not_also_an_error()
    {
        var csv = "Date,Symbol,Exchange,Type,Quantity,Price,Currency\n01/01/2025,AAPL,NAS,Split,30,,USD";
        var row = Assert.Single(NavexaParser.ParseTrades(csv, new NavexaOptions("AUD", Today: new DateTime(2026, 10, 15))).Rows);
        Assert.Equal(ImportRowStatus.Unsupported, row.Status);
    }
}
