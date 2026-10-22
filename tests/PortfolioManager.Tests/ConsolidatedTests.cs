using PortfolioManager.Core.Holdings;
using PortfolioManager.Core.Reports;

namespace PortfolioManager.Tests;

public class ConsolidatedTests
{
    private static Holding H(string code, decimal units, decimal value) => new(code, "ASX", units, units == 0 ? 0 : value / units, value, 0, 0, 0, "");

    private static EntityFigures E(int id, string ccy, decimal shares, decimal cash, params Holding[] holdings) =>
        new(id, $"P{id}", "Individual", ccy, shares, cash, 100, 50, holdings);

    [Fact]
    public void Holdings_are_combined_across_entities_with_shares_of_the_group()
    {
        var r = ConsolidatedReport.Build([
            E(1, "AUD", 3000, 1000, H("CBA", 10, 1000), H("BHP", 20, 1500)),
            E(2, "AUD", 1000, 0, H("cba ", 5, 1000), H("OLD", 0, 0)),
        ]);
        Assert.True(r.OneCurrency);
        Assert.Equal(5000m, r.TotalValue);
        Assert.Equal(["CBA", "BHP"], r.Holdings.Select(h => h.StockCode));
        var cba = r.Holdings[0];
        Assert.Equal((2000m, 15m), (cba.Value, cba.Units));
        Assert.Equal(1000m, cba.ValueByEntity[2]);
        Assert.Equal(0.4, r.ShareOf(cba.Value));
        Assert.Equal(200m, r.Income);
    }

    [Fact]
    public void Mixed_currencies_have_no_group_shares()
    {
        var r = ConsolidatedReport.Build([E(1, "AUD", 100, 0), E(2, "NZD", 100, 0)]);
        Assert.False(r.OneCurrency);
        Assert.Null(r.ShareOf(100));
    }

    [Fact]
    public void Portfolio_list_is_xml_without_repeats() =>
        Assert.Equal("<ps><p id=\"3\" /><p id=\"7\" /></ps>", ConsolidatedService.PortfoliosXml([3, 7, 3]));

    [Theory]
    [InlineData("", new[] { 1, 2 }, "Give the group a name.")]
    [InlineData("Smiths", new[] { 1, 99 }, "Pick at least two portfolios.")]
    public async Task Save_checks_before_writing(string name, int[] ids, string error)
    {
        var service = new ConsolidatedService(null!, null!, null!, null!, null!);
        var result = await service.SaveAsync(1, 0, name, ids, new HashSet<int> { 1, 2 });
        Assert.Equal(error, result.Error);
    }
}
