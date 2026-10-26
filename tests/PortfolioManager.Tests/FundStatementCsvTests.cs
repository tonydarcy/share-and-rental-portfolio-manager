using PortfolioManager.Core.Imports;

namespace PortfolioManager.Tests;

public class FundStatementCsvTests
{
    [Fact]
    public void Reads_the_template_headings()
    {
        var text = FundStatementCsv.Template + "\nASX,VAS,2026,1000,400,171.43,50,20,10,0,200,30,100,15,0,0,90.00,Annual statement";
        var preview = FundStatementCsv.Parse(text);

        var s = Assert.Single(preview.ValidRows);
        Assert.Equal(("ASX", "VAS", 2026, new DateOnly(2026, 6, 30)), (s.ExchangeCode, s.StockCode, s.Year, s.YearEnd));
        Assert.Equal((1000m, 400m, 171.43m, 50m, 20m, 10m), (s.CashDistribution, s.Franked, s.FrankingCredits, s.Unfranked, s.Interest, s.OtherIncome));
        Assert.Equal((200m, 30m, 100m, 15m, 90m), (s.DiscountedGain, s.OtherGain, s.ForeignIncome, s.ForeignTaxCredits, s.CostBaseDecrease));
        Assert.Equal("Annual statement", s.Comment);
    }

    [Fact]
    public void Finds_registry_style_columns_below_lines_about_the_investor()
    {
        var text = """
            Annual Tax Statement export
            Investor,J Citizen
            ASX Code,Financial Year,Net cash distribution,Franked distributions,Share of franking credits,Discounted capital gain,"AMIT cost base net amount - shortfall",Foreign income tax offset
            STW,2025-26,"$1,250.00",800.00,342.86,100.00,25.50,0
            """;
        var s = Assert.Single(FundStatementCsv.Parse(text).ValidRows);
        Assert.Equal(("ASX", "STW", 2026), (s.ExchangeCode, s.StockCode, s.Year));
        Assert.Equal((1250m, 800m, 342.86m, 100m, 25.50m, 0m), (s.CashDistribution, s.Franked, s.FrankingCredits, s.DiscountedGain, s.CostBaseIncrease, s.CostBaseDecrease));
    }

    [Fact]
    public void A_single_net_amit_column_that_is_negative_is_an_increase()
    {
        var s = Assert.Single(FundStatementCsv.Parse("Code,Year,Cash distribution,Cost base decrease\nVGS,FY2026,100,(12.40)").ValidRows);
        Assert.Equal((12.40m, 0m), (s.CostBaseIncrease, s.CostBaseDecrease));
    }

    [Fact]
    public void Lines_with_problems_are_shown_and_left_out()
    {
        var text = "Code,Year,Cash distribution,Franked\nVAS,2026,100,50\nVAS,2026,100,50\nVGS,last year,100,0\nIOZ,2026,abc,0\nNDQ,2026,-5,0";
        var preview = FundStatementCsv.Parse(text);
        Assert.Equal(1, preview.ValidCount);
        Assert.Contains(preview.Lines[1].Errors, e => e.Contains("more than once"));
        Assert.Contains(preview.Lines[2].Errors, e => e.Contains("financial year"));
        Assert.Contains(preview.Lines[3].Errors, e => e.Contains("isn't an amount"));
        Assert.Contains(preview.Lines[4].Errors, e => e.Contains("negative"));
    }

    [Fact]
    public void Warns_when_the_parts_dont_tie_to_the_cash()
    {
        var line = Assert.Single(FundStatementCsv.Parse("Code,Year,Cash distribution,Franked\nVAS,2026,100,50").Lines);
        Assert.True(line.IsValid);
        Assert.Contains(line.Warnings, w => w.Contains("less than the cash"));
    }

    [Fact]
    public void A_file_without_the_needed_columns_is_refused() =>
        Assert.NotNull(FundStatementCsv.Parse("Fund,Amount\nVAS,100").FileError);

    [Theory]
    [InlineData("2026", 2026)]
    [InlineData("2025-26", 2026)]
    [InlineData("2025/2026", 2026)]
    [InlineData("FY2026", 2026)]
    [InlineData("FY26", 2026)]
    [InlineData("30/06/2026", 2026)]
    [InlineData("2099-00", 2100)]
    [InlineData("2025-27", null)]
    [InlineData("soon", null)]
    public void Reads_the_financial_year(string text, int? year) =>
        Assert.Equal(year, FundStatementCsv.Year(text));
}
