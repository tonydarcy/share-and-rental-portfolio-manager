using PortfolioManager.Core.TaxWork;

namespace PortfolioManager.Tests;

public class PrefillTests
{
    [Fact]
    public void Reads_interest_and_dividends_by_column_heading()
    {
        var (items, problems) = PrefillParser.Parse("""
            Type,Payer,Amount,Franked,Unfranked,Franking credit,Tax withheld
            Interest,Big Bank,"1,412.50",,,,10
            Dividend,BHP Group,,1200.00,0,514.29,
            Managed fund distribution,Vanguard,800,,,,
            Something,Bad,abc,,,,
            """);
        Assert.Contains(items, i => i.Category == PrefillCategories.Interest && i.Amount == 1412.50m && i.Payer == "Big Bank");
        Assert.Contains(items, i => i.Category == PrefillCategories.InterestTfn && i.Amount == 10);
        Assert.Contains(items, i => i.Category == PrefillCategories.Franked && i.Amount == 1200);
        Assert.Contains(items, i => i.Category == PrefillCategories.FrankingCredit && i.Amount == 514.29m);
        Assert.Contains(items, i => i.Category == PrefillCategories.Trust && i.Amount == 800);
        Assert.DoesNotContain(items, i => i.Category == PrefillCategories.Unfranked);
        Assert.Single(problems);
    }

    [Fact]
    public void Reads_pasted_tab_separated_tables()
    {
        var (items, _) = PrefillParser.Parse("Income type\tPayer\tGross interest\nInterest\tCredit Union\t$55.20");
        var i = Assert.Single(items);
        Assert.Equal(55.20m, i.Amount);
        Assert.Equal(PrefillCategories.Interest, i.Category);
    }

    [Fact]
    public void Needs_an_amount_column() =>
        Assert.NotEmpty(PrefillParser.Parse("Type,Payer\nInterest,Bank").Problems);

    [Theory]
    [InlineData("Employee share scheme", PrefillCategories.Ess)]
    [InlineData("ESS statement", PrefillCategories.Ess)]
    [InlineData("Business income", PrefillCategories.Other)]
    [InlineData("Share disposal", PrefillCategories.ShareSale)]
    public void Kinds(string text, string kind) => Assert.Equal(kind, PrefillParser.Kind(text, false));

    [Fact]
    public void Xml_escapes_payer_names() =>
        Assert.Contains("p=\"A &amp; B\"", PrefillParser.ToXml([new PrefillItem(PrefillCategories.Interest, "A & B", 1)]));
}
