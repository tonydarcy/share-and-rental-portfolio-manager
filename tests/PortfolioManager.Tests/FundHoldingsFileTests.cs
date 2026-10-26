using PortfolioManager.Core.Reports;

namespace PortfolioManager.Tests;

public class FundHoldingsFileTests
{
    [Fact]
    public void Reads_an_issuer_file_with_lines_about_the_fund_above_the_header()
    {
        var text = """
            iShares Core S&P/ASX 200 ETF
            Fund Holdings as of,"24/Oct/2026"
            Inception Date,"06/Dec/2010"

            Ticker,Name,Sector,Asset Class,Market Value,Weight (%),Location
            CBA,COMMONWEALTH BANK OF AUSTRALIA,Financials,Equity,"1,000,000.00",10.50,Australia
            BHP,BHP GROUP LTD,Materials,Equity,"900,000.00",9.00,Australia
            RMD,RESMED INC,Health Care,Equity,"100,000.00",0.50,United States
            AUD,AUD CASH,Cash and/or Derivatives,Cash,"50,000.00",0.25,Australia

            "The content contained herein is owned or licensed by BlackRock."
            """;
        var read = FundHoldingsFile.Parse(text);

        Assert.Null(read.Error);
        Assert.Equal(4, read.Lines);
        var sectors = read.Weights.Where(w => w.Kind == LookThrough.Sector).ToDictionary(w => w.Name, w => w.Weight);
        Assert.Equal(0.105m, sectors["Financials"]);
        Assert.Equal(0.0025m, sectors["Cash"]);
        var countries = read.Weights.Where(w => w.Kind == LookThrough.Country).ToDictionary(w => w.Name, w => w.Weight);
        Assert.Equal(0.1975m, countries["Australia"]);
        Assert.Equal(0.005m, countries["United States"]);
        var holdings = read.Weights.Where(w => w.Kind == LookThrough.Holding).ToList();
        Assert.Equal(["CBA", "BHP", "RMD"], holdings.Select(h => h.Name));
        Assert.Equal("COMMONWEALTH BANK OF AUSTRALIA", holdings[0].Label);
        Assert.Contains(read.Warnings, w => w.Contains("not 100%"));
    }

    [Fact]
    public void Market_values_give_the_weights_when_there_is_no_weight_column()
    {
        var text = """
            Holding name,Code,Market value ($),Sector
            Commonwealth Bank,CBA AU,"$750.00",Financial Services
            Wesfarmers,WES.AX,"$250.00",Consumer Cyclical
            """;
        var read = FundHoldingsFile.Parse(text);

        Assert.Null(read.Error);
        Assert.Equal([0.75m, 0.25m], read.Weights.Where(w => w.Kind == LookThrough.Holding).Select(w => w.Weight));
        Assert.Equal(["Financials", "Consumer Discretionary"], read.Weights.Where(w => w.Kind == LookThrough.Sector).Select(w => w.Name));
        Assert.Contains(read.Warnings, w => w.Contains("no country column"));
        Assert.DoesNotContain(read.Warnings, w => w.Contains("not 100%"));
    }

    [Fact]
    public void Weights_given_as_fractions_are_used_as_they_are()
    {
        var read = FundHoldingsFile.Parse("Name,Weight,Country\nApple,0.6,United States\nNestle,0.4,Switzerland\n");
        Assert.Equal([0.6m, 0.4m], read.Weights.Where(w => w.Kind == LookThrough.Country).Select(w => w.Weight));
        Assert.Equal(["APPLE", "NESTLE"], read.Weights.Where(w => w.Kind == LookThrough.Holding).Select(w => w.Name));
    }

    [Fact]
    public void Keeps_only_the_largest_holdings_by_name()
    {
        var lines = string.Join("\n", Enumerable.Range(1, 40).Select(i => $"S{i:00},Stock {i},2.5"));
        var read = FundHoldingsFile.Parse("Code,Name,% of net assets\n" + lines);
        Assert.Equal(FundHoldingsFile.TopHoldings, read.Weights.Count(w => w.Kind == LookThrough.Holding));
        Assert.Equal(40, read.Lines);
    }

    [Fact]
    public void The_result_round_trips_through_the_look_through_box()
    {
        var read = FundHoldingsFile.Parse("Ticker,Name,Weight (%),Sector,Country\nCBA,Commonwealth Bank,60,Financials,Australia\nBHP,BHP Group,40,Materials,Australia");
        var (weights, errors) = LookThrough.Parse(LookThrough.ToText(read.Weights));
        Assert.Empty(errors);
        Assert.Equal(read.Weights, weights);
    }

    [Theory]
    [InlineData("Just some text\nwith no table")]
    [InlineData("Ticker,Name,Weight (%)\n")]
    public void A_file_without_holdings_is_refused(string text) =>
        Assert.NotNull(FundHoldingsFile.Parse(text).Error);

    [Theory]
    [InlineData("CBA AU", "CBA")]
    [InlineData("ASX:CBA", "CBA")]
    [InlineData("cba.ax", "CBA")]
    [InlineData("AAPL US", "AAPL")]
    [InlineData("-", "")]
    public void Tickers_lose_their_market_suffix(string code, string expected) =>
        Assert.Equal(expected, FundHoldingsFile.Ticker(code));
}
