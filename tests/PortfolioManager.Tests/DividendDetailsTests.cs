using PortfolioManager.Core.CorporateActions;
using PortfolioManager.Core.Imports;

namespace PortfolioManager.Tests;

public class DividendDetailsTests
{
    private static DividendSuggestion Dividend(decimal? drpPrice = null, FrankingGuess? franking = null) =>
        new(7, "ASX", "CBA", new DateOnly(2026, 8, 20), new DateOnly(2026, 9, 25), 2.25m, "AUD", "Final", 1000, franking, false, "EODHD", drpPrice);

    [Fact]
    public void Reads_the_details_file()
    {
        var preview = DividendDetailsCsv.Parse("""
            Exchange,Code,Ex date,Franked %,DRP price,Amount,Payment date
            asx,cba,2026-08-20,100%,165.20,2.25,29/09/2026
            ASX,WES,20/08/2026,50,,
            ASX,VAS,2026-10-01,,98.4321
            ASX,BAD,2026-10-01,,
            ASX,XYZ,2026-13-01,100,
            ASX,ABC,2026-10-01,120,
            asx,CBA,2026-08-20,100,
            """);

        Assert.Equal(3, preview.ValidCount);
        Assert.Equal(4, preview.ErrorCount);
        var cba = preview.ValidRows.First();
        Assert.Equal(("ASX", "CBA", new DateOnly(2026, 8, 20), 100m, 165.20m, 2.25m, new DateOnly(2026, 9, 29)),
            (cba.ExchangeCode, cba.StockCode, cba.ExDate, cba.FrankedPercent, cba.DrpPrice, cba.AmountPerUnit, cba.PaymentDate));
        Assert.Null(preview.ValidRows.Single(r => r.StockCode == "VAS").FrankedPercent);
        Assert.Contains(preview.Lines.Single(l => l.Text.StartsWith("ASX,BAD")).Errors, e => e.Contains("franked percentage, the DRP price"));
        Assert.Contains(preview.Lines.Last().Errors, e => e.Contains("more than once"));
    }

    [Fact]
    public void Announced_franking_wins_and_says_where_it_came_from()
    {
        var f = AnnouncedPayouts.FromAnnouncement(100m, "ASX announcement")!;
        Assert.Equal((1m, FrankingGuess.StandardCreditRate), (f.FrankedShare, f.CreditPerFrankedDollar));
        Assert.Equal("fully franked, as announced (ASX announcement)", f.Basis);
        Assert.Equal("50% franked, as announced", AnnouncedPayouts.FromAnnouncement(50m, null)!.Basis);
        Assert.Null(AnnouncedPayouts.FromAnnouncement(null, "x"));
        Assert.Null(AnnouncedPayouts.FromAnnouncement(150m, "x"));

        var p = AnnouncedPayouts.ToPayout(Dividend(franking: f));
        Assert.Equal((2250m, 2250m, 0m, 964.29m), (p.Total, p.Franked, p.Unfranked, p.FrankingCredit));
        Assert.Null(AnnouncedPayouts.NeedsReview(Dividend(franking: f), "AUD"));
    }

    [Fact]
    public void Drp_units_are_the_whole_units_the_dividend_buys()
    {
        Assert.Equal((13m, 165.20m), AnnouncedPayouts.DrpUnits(Dividend(165.20m))); // 2,250 / 165.20 = 13.6
        Assert.Null(AnnouncedPayouts.DrpUnits(Dividend()));
    }
}
