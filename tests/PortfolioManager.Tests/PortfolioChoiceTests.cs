using PortfolioManager.Core.Dashboard;
using PortfolioManager.Core.Portfolios;

namespace PortfolioManager.Tests;

public class PortfolioChoiceTests
{
    private static readonly PortfolioSummary[] Accessible = [new(7, "Super fund"), new(3, "Personal"), new(9, "Trust")];

    [Fact]
    public void Uses_the_requested_portfolio_when_permitted() =>
        Assert.Equal(9, PortfolioService.Choose(Accessible, userId: 3, requestedId: 9)!.PortfolioId);

    [Fact]
    public void Ignores_a_requested_portfolio_the_user_cannot_open() =>
        Assert.Equal(3, PortfolioService.Choose(Accessible, userId: 3, requestedId: 42)!.PortfolioId);

    [Fact]
    public void Falls_back_to_the_first_when_own_portfolio_is_not_listed() =>
        Assert.Equal(7, PortfolioService.Choose(Accessible, userId: 1, requestedId: null)!.PortfolioId);

    [Fact]
    public void Returns_null_when_nothing_is_accessible() =>
        Assert.Null(PortfolioService.Choose([], userId: 1, requestedId: 1));

    [Fact]
    public void Projected_payouts_are_totalled_by_month_in_order()
    {
        var months = DashboardService.ByMonth([
            new("ASX", "CBA", new DateTime(2027, 3, 20), 100m),
            new("ASX", "BHP", new DateTime(2026, 11, 2), 50m),
            new("ASX", "WES", new DateTime(2027, 3, 1), 25.5m),
        ]);
        Assert.Equal([(new DateTime(2026, 11, 1), 50m), (new DateTime(2027, 3, 1), 125.5m)], months);
    }
}
