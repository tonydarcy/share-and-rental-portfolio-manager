using PortfolioManager.Core.Reports;

namespace PortfolioManager.Tests;

public class DiversityMapTests
{
    private static Dictionary<string, object?> Row(string sector, string industry, object? value, int order = 1) => new()
    {
        ["Sector"] = sector,
        ["Industry"] = industry,
        ["Value"] = value,
        ["Percentage"] = "ignored",
        ["RowOrder"] = order,
    };

    [Fact]
    public void Uses_the_total_row_and_groups_by_sector()
    {
        var report = DiversityService.Map([
            Row("Materials", "Metals & Mining", 300d),
            Row("Financials", "Banks", 450d),
            Row("Materials", "Chemicals", 250d),
            Row("Total:", "", 1000d, 2),
        ]);

        Assert.Equal(1000m, report.Total);
        Assert.Equal(3, report.Lines.Count);
        Assert.Equal("Banks", report.Lines[0].Industry);
        Assert.Equal(0.45m, report.Lines[0].Share);
        Assert.Equal(["Materials", "Financials"], report.Sectors.Select(s => s.Sector));
        Assert.Equal(550m, report.Sectors[0].Value);
        Assert.Equal(0.55m, report.Sectors[0].Share);
    }

    [Fact]
    public void Missing_values_and_names_are_handled()
    {
        var report = DiversityService.Map([
            Row("Energy", "Oil", 100d),
            Row("", "", DBNull.Value),
            Row("Total:", "", DBNull.Value, 2),
        ]);

        Assert.Equal(100m, report.Total);
        Assert.Contains(report.Lines, l => l.Sector == "Unknown" && l.Value == 0);
    }

    [Fact]
    public void No_holdings_gives_an_empty_report()
    {
        var report = DiversityService.Map([Row("Total:", "", DBNull.Value, 2)]);
        Assert.Empty(report.Lines);
        Assert.Equal(0m, report.Total);
    }
}
