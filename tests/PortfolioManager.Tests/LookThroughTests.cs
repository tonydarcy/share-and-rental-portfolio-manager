using PortfolioManager.Core.Prices;
using PortfolioManager.Core.Reports;

namespace PortfolioManager.Tests;

public class LookThroughTests
{
    private static FundExposure Fund(string code, params ExposureWeight[] w) => new("ASX", code, w, null, "EODHD", false);

    [Fact]
    public void Fund_value_is_spread_and_a_company_held_both_ways_is_one_line()
    {
        var holdings = new[]
        {
            new LookThroughHolding("ASX", "VAS", 10_000, null),
            new LookThroughHolding("ASX", "CBA", 2_000, "Financials"),
            new LookThroughHolding("NASDAQ", "MSFT", 1_000, "Information Technology"),
        };
        var funds = new Dictionary<(string, string), FundExposure>
        {
            [("ASX", "VAS")] = Fund("VAS",
                new("Sector", "Financial Services", 0.3m), new("Sector", "Basic Materials", 0.2m),
                new("Country", "Australia", 1m),
                new("Holding", "CBA", 0.1m, "Commonwealth Bank")),
        };
        var r = LookThrough.Build(holdings, funds);

        Assert.Equal(13_000m, r.Total);
        var fin = r.Sectors.Single(s => s.Name == "Financials");
        Assert.Equal((5_000m, 3_000m), (fin.Value, fin.ThroughFunds));
        Assert.Equal(5_000m, r.Sectors.Single(s => s.Name == "Unknown").Value);
        Assert.Equal("Unknown", r.Sectors[^1].Name);
        Assert.Equal(12_000m, r.Countries.Single(c => c.Name == "Australia").Value);
        Assert.Equal(1_000m, r.Countries.Single(c => c.Name == "United States").Value);
        var cba = r.Companies.Single(c => c.Name == "CBA");
        Assert.Equal((3_000m, 1_000m, "Commonwealth Bank"), (cba.Value, cba.ThroughFunds, cba.Label));
        Assert.Equal(9_000m, r.Companies.Single(c => c.Name == LookThrough.RestOfFund).Value);
        Assert.Equal(["VAS"], r.FundsCovered);
    }

    [Fact]
    public void Funds_without_data_count_as_one_holding_and_are_listed()
    {
        var r = LookThrough.Build([new LookThroughHolding("FUND", "MYFUND", 500, null)],
            new Dictionary<(string, string), FundExposure> { [("FUND", "MYFUND")] = Fund("MYFUND", new ExposureWeight("None", "-", 0)) });
        Assert.Equal(["MYFUND"], r.FundsWithoutData);
        Assert.Equal(500m, r.Companies.Single().Value);
    }

    [Fact]
    public void Pasted_lines_are_read_and_checked()
    {
        var (w, errors) = LookThrough.Parse("Kind,Name,Weight\nSector, Financials, 31.5\nRegion, Asia, 20%\nHolding, cba, 10, Commonwealth Bank\nStock, X, 1\nSector, Energy, 120");
        Assert.Equal(3, w.Count);
        Assert.Equal(new ExposureWeight("Country", "Asia", 0.2m), w[1]);
        Assert.Equal(new ExposureWeight("Holding", "CBA", 0.1m, "Commonwealth Bank"), w[2]);
        Assert.Equal(2, errors.Count);
        Assert.Equal(["Sector, Financials, 31.5", "Country, Asia, 20", "Holding, CBA, 10, Commonwealth Bank"], LookThrough.ToText(w).Split('\n'));
    }

    [Fact]
    public void Weights_over_100_percent_are_refused()
    {
        var (_, errors) = LookThrough.Parse("Sector, A, 60\nSector, B, 50");
        Assert.Contains(errors, e => e.Contains("more than 100%"));
    }

    [Fact]
    public void Eodhd_etf_data_is_read()
    {
        const string json = """
            {"ISIN":"AU000000VAS1","Sector_Weights":{"Financial Services":{"Equity_%":"31.25","Relative_to_Category":"1"},"Energy":{"Equity_%":4.5}},
             "World_Regions":{"Australasia":{"Equity_%":"98.0"},"Asia":{"Equity_%":"0"}},
             "Top_10_Holdings":{"CBA.AU":{"Code":"CBA","Exchange":"AU","Name":"Commonwealth Bank of Australia","Assets_%":10.1}}}
            """;
        var r = EodhdPriceProvider.ParseFundExposure(json);
        Assert.True(r.IsOk);
        Assert.Equal(4, r.Items.Count);
        Assert.Equal(new ExposureWeight("Sector", "Financial Services", 0.3125m), r.Items[0]);
        Assert.Equal(new ExposureWeight("Holding", "CBA", 0.101m, "Commonwealth Bank of Australia"), r.Items[3]);
        Assert.Empty(EodhdPriceProvider.ParseFundExposure("\"NA\"").Items);
    }

    [Fact]
    public void Weights_are_sent_as_xml() =>
        Assert.Equal("<w><e k=\"Holding\" n=\"CBA\" l=\"Commonwealth Bank\" v=\"0.1\" /></w>",
            LookThrough.WeightsXml([new ExposureWeight("Holding", "CBA", 0.1m, "Commonwealth Bank")]));
}
