using PortfolioManager.Core.Clients;
using PortfolioManager.Core.Practice;
using PortfolioManager.Core.Regions;

namespace PortfolioManager.Tests;

public class PracticeYearEndTests
{
    private static PracticeClientYear Client(string name, RegionSettings region) =>
        new(new ClientListItem(1, name, "Individual", true, name, ClientEntityTypes.Individual, null, null, null), "NotStarted", null, 0, 0, 0, 0, 0, 0, 0, null,
            region, 2026);

    [Fact]
    public void Each_client_year_ends_on_its_own_countrys_date()
    {
        var au = Client("Aussie", RegionSettings.Australia);
        var uk = Client("Brit", RegionSettings.From("GB", null, null, null, null));
        var us = Client("Yank", RegionSettings.From("US", null, null, null, null));
        Assert.Equal(new DateOnly(2026, 6, 30), au.YearEnd);
        Assert.Equal(new DateOnly(2026, 4, 5), uk.YearEnd);
        Assert.Equal(new DateOnly(2026, 12, 31), us.YearEnd);
        Assert.Equal("2025-26", au.YearLabel);
        Assert.Equal("2026", us.YearLabel);
        Assert.Equal(0, au.DaysToYearEnd(new DateOnly(2026, 6, 30)));
        Assert.True(uk.DaysToYearEnd(new DateOnly(2026, 6, 30)) < 0);
    }

    [Fact]
    public void Clients_are_grouped_by_country_with_australia_first_and_year_ends_in_order()
    {
        var rows = new[]
        {
            Client("Yank", RegionSettings.From("US", null, null, null, null)),
            Client("Zed", RegionSettings.Australia),
            Client("Brit", RegionSettings.From("GB", null, null, null, null)),
            Client("Abe", RegionSettings.Australia),
        };
        var groups = PracticeDashboardService.ByCountry(rows);
        Assert.Equal(["Australia", "United Kingdom", "United States"], groups.Select(g => g.Key));
        Assert.Equal(["Abe", "Zed"], groups[0].Select(r => r.Client.LegalName));
    }

    [Fact]
    public void A_client_without_region_settings_is_australian()
    {
        var row = Client("Old", RegionSettings.Australia) with { Region = null };
        Assert.Equal("Australia", row.CountryName);
        Assert.Equal(new DateOnly(2026, 6, 30), row.YearEnd);
    }
}
