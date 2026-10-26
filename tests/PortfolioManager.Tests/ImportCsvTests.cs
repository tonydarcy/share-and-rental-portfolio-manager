using PortfolioManager.Core.Imports;

namespace PortfolioManager.Tests;

public class CsvSplitTests
{
    [Fact]
    public void Splits_plain_and_quoted_fields()
    {
        Assert.Equal(["ASX", "ANZ", "Anz, Group", "say \"hi\"", ""], CsvImport.Split("ASX, ANZ ,\"Anz, Group\",\"say \"\"hi\"\"\","));
    }

    [Fact]
    public void Lines_skip_blanks_keep_numbers_and_drop_the_bom()
    {
        var lines = CsvImport.Lines("﻿a,b\r\n\r\nc,d\n").ToList();
        Assert.Equal([(1, "a,b"), (3, "c,d")], lines);
    }

    [Theory]
    [InlineData(0, "a.csv", true)]
    [InlineData(100, "a.csv", false)]
    [InlineData(100, "A.TXT", false)]
    [InlineData(100, "a.xlsx", true)]
    [InlineData(CsvImport.MaxFileBytes + 1, "a.csv", true)]
    public void Checks_size_and_type(long length, string name, bool rejected) =>
        Assert.Equal(rejected, CsvImport.CheckFile(length, name) is not null);
}

public class SharePriceCsvTests
{
    [Fact]
    public void Reads_commsec_and_eod_data_lines()
    {
        var preview = SharePriceCsv.Parse("BHP,20261014,45.1,45.8,44.95,45.62,8123456\ncba,261014,150,151,149,150.5,1000\n");

        Assert.Equal(2, preview.ValidCount);
        var bhp = preview.Lines[0].Value!;
        Assert.Equal(new DateTime(2026, 10, 14), bhp.Date);
        Assert.Equal(45.62m, bhp.Close);
        Assert.Equal(8123456m, bhp.Volume);
        Assert.Equal("CBA", preview.Lines[1].Value!.StockCode);
        Assert.Equal(new DateTime(2026, 10, 14), preview.Lines[1].Value!.Date);
    }

    [Fact]
    public void Non_numeric_open_high_low_volume_become_zero_like_the_legacy_upload()
    {
        var row = SharePriceCsv.Parse("XYZ,20261014,-,n/a,,1.5,").Lines[0].Value!;
        Assert.Equal(0m, row.Open);
        Assert.Equal(0m, row.Volume);
        Assert.Equal(1.5m, row.Close);
    }

    [Theory]
    [InlineData("BHP,20261014,1,1,1,0,10")]          // no close price
    [InlineData("BHP,20261314,1,1,1,1,10")]          // month 13
    [InlineData("BHP,2026-10-14,1,1,1,1,10")]        // wrong date format
    [InlineData("BHP,20261014,1,1,1,1")]             // too few fields
    [InlineData(",20261014,1,1,1,1,10")]             // no code
    [InlineData("BHP,20261014,-1,1,1,1,10")]         // negative
    public void Flags_bad_lines(string line)
    {
        var preview = SharePriceCsv.Parse("AAA,20261014,1,1,1,1,1\n" + line);
        Assert.Equal(1, preview.ErrorCount);
        Assert.False(preview.Lines[1].IsValid);
        Assert.NotEmpty(preview.Lines[1].Errors);
        Assert.Equal(2, preview.Lines[1].LineNumber);
    }

    [Fact]
    public void Flags_the_same_code_and_date_twice()
    {
        var preview = SharePriceCsv.Parse("BHP,20261014,1,1,1,1,1\nBHP,20261014,1,1,1,2,1");
        Assert.Equal(1, preview.ValidCount);
        Assert.Equal(1, preview.ErrorCount);
    }

    [Fact]
    public void Skips_a_header_line()
    {
        var preview = SharePriceCsv.Parse("Code,Date,Open,High,Low,Close,Volume\nBHP,20261014,1,1,1,1,1");
        Assert.Single(preview.Lines);
        Assert.Equal(0, preview.ErrorCount);
    }

    [Fact]
    public void An_empty_file_is_an_error() => Assert.NotNull(SharePriceCsv.Parse("\n\n").FileError);

    [Theory]
    [InlineData("ASX", true)]
    [InlineData("nasdaq", true)]
    [InlineData("", false)]
    [InlineData("AS X", false)]
    [InlineData("ASX'; DROP", false)]
    public void Checks_the_exchange_code(string code, bool ok) => Assert.Equal(ok, SharePriceCsv.CheckExchangeCode(code) is null);
}

public class ShareTradeCsvTests
{
    private static readonly DateTime Today = new(2026, 10, 15);

    private static string RepoFile(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PortfolioManager.slnx")))
            dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? throw new DirectoryNotFoundException("repo root"), name);
    }

    [Fact]
    public void Reads_the_sample_upload_file()
    {
        var preview = ShareTradeCsv.Parse(File.ReadAllText(RepoFile(Path.Combine("samples", "share-trades.csv"))), Today);

        Assert.Null(preview.FileError);
        Assert.Equal(0, preview.ErrorCount);
        Assert.True(preview.ValidCount >= 4);
        var anz = preview.Lines[0].Value!;
        Assert.Equal("ASX", anz.StockExchangeCode);
        Assert.Equal("ANZ", anz.StockCode);
        Assert.Equal("Buy", anz.Type);
        Assert.Equal(new DateTime(2024, 2, 29), anz.Date);
        Assert.Equal(147, anz.Quantity);
        Assert.Equal(28.345714m, anz.Price);
        Assert.Equal(2m, anz.Brokerage);
        Assert.Equal("AUD", anz.Currency);
        Assert.Equal(1m, anz.ExchangeRate);
        Assert.Equal("Imported 8-Aug-24", anz.Comment);
        Assert.Equal("Sell", preview.Lines[1].Value!.Type);
    }

    [Theory]
    [InlineData("ASX,BHP,BHP Group,buy,29-Feb-2024,10,45,0,aud,1,")]
    [InlineData("ASX,BHP,,Buy,29/02/2024,10,45,,,,")]
    [InlineData("ASX,BHP,\"BHP, Group\",SELL,2024-02-29,10,45.5,9.95,AUD,1,\"Sold, partly\"")]
    public void Accepts_the_usual_variations(string line)
    {
        var preview = ShareTradeCsv.Parse(line, Today);
        Assert.Equal(1, preview.ValidCount);
        var t = preview.Lines[0].Value!;
        Assert.Equal(new DateTime(2024, 2, 29), t.Date);
        Assert.Equal("AUD", t.Currency);
        Assert.Equal(1m, t.ExchangeRate);
    }

    [Theory]
    [InlineData("ASX,BHP,BHP,Hold,2024-02-29,10,45,0,AUD,1,x")]       // type
    [InlineData("ASX,BHP,BHP,Buy,2024-02-30,10,45,0,AUD,1,x")]        // date
    [InlineData("ASX,BHP,BHP,Buy,2026-10-16,10,45,0,AUD,1,x")]        // future
    [InlineData("ASX,BHP,BHP,Buy,2024-02-29,10.123456789,45,0,AUD,1,x")] // more than 8 decimal places
    [InlineData("ASX,BHP,BHP,Sell,2024-02-29,-10,45,0,AUD,1,x")]      // negative quantity
    [InlineData("ASX,BHP,BHP,Buy,2024-02-29,10,abc,0,AUD,1,x")]       // price
    [InlineData("ASX,BHP,BHP,Buy,2024-02-29,10,45,-2,AUD,1,x")]       // brokerage
    [InlineData("ASX,BHP,BHP,Buy,2024-02-29,10,45,0,AUSD,1,x")]       // currency
    [InlineData("ASX,BHP,BHP,Buy,2024-02-29,10,45,0,USD,0,x")]        // exchange rate
    [InlineData("ASX,,BHP,Buy,2024-02-29,10,45,0,AUD,1,x")]           // code
    [InlineData("ASX,BHP,BHP,Buy,2024-02-29,10,45,0,AUD,1")]          // field count
    public void Flags_bad_lines(string line)
    {
        var preview = ShareTradeCsv.Parse("ASX,CBA,CBA,Buy,2024-01-02,1,100,0,AUD,1,ok\n" + line, Today);
        Assert.Equal(1, preview.ValidCount);
        Assert.Equal(1, preview.ErrorCount);
        Assert.Null(preview.Lines[1].Value);
    }

    [Fact]
    public void Skips_a_header_line()
    {
        var preview = ShareTradeCsv.Parse(
            "Exchange,Code,Name,Type,Date,Quantity,Price,Brokerage,Currency,Rate,Comment\nASX,CBA,CBA,Buy,2024-01-02,1,100,0,AUD,1,ok", Today);
        Assert.Single(preview.Lines);
        Assert.Equal(1, preview.ValidCount);
    }

    [Fact]
    public void Warns_about_trades_already_on_file_or_repeated()
    {
        var preview = ShareTradeCsv.Parse(
            "ASX,CBA,CBA,Buy,2024-01-02,1,100,0,AUD,1,a\nASX,CBA,CBA,Buy,2024-01-02,1,100,0,AUD,1,b\nASX,BHP,BHP,Buy,2024-01-02,5,40,0,AUD,1,c", Today);
        var existing = new HashSet<string> { ShareTradeRow.TradeKey("asx", "bhp", "BUY", new DateTime(2024, 1, 2), 5, 40.00m) };

        var flagged = ShareTradeCsv.FlagDuplicates(preview, existing);

        Assert.Empty(flagged.Lines[0].Warnings);
        Assert.Single(flagged.Lines[1].Warnings);
        Assert.Single(flagged.Lines[2].Warnings);
        Assert.Equal(3, flagged.ValidCount);
    }
}
