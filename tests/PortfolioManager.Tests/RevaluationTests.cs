using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Clients;
using PortfolioManager.Core.Tax;

namespace PortfolioManager.Tests;

public class RevaluationTests
{
    private static UnrealisedGainLine Open(string code, decimal units, decimal? price, string exchange = "ASX") =>
        new(exchange, code, units, 0, 0, price, 0, 0);

    private static Dictionary<string, decimal> Costs(params (string Key, decimal Cost)[] costs) =>
        costs.ToDictionary(c => c.Key, c => c.Cost, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Each_holding_is_market_value_less_its_ledger_cost()
    {
        var holdings = RevaluationPlan.Combine(Costs(("BHP.ASX", 30000m), ("CBA.ASX", 10000m)),
            [Open("BHP", 1000, 45m), Open("CBA", 100, 90m)]);

        var bhp = holdings.Single(h => h.Key == "BHP.ASX");
        Assert.Equal(45000m, bhp.MarketValue);
        Assert.Equal(15000m, bhp.Adjustment);
        Assert.Equal(-1000m, holdings.Single(h => h.Key == "CBA.ASX").Adjustment);
    }

    [Fact]
    public void A_typed_price_wins_over_the_one_on_file()
    {
        var holdings = RevaluationPlan.Combine(Costs(("BHP.ASX", 30000m)), [Open("BHP", 1000, 45m)],
            new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase) { ["bhp.asx"] = 40m });

        Assert.Equal(40000m, holdings.Single().MarketValue);
    }

    [Fact]
    public void A_holding_without_a_price_stops_the_posting()
    {
        var plan = new RevaluationPlan(new DateOnly(2026, 6, 30),
            RevaluationPlan.Combine(Costs(("XYZ.ASX", 500m)), [Open("XYZ", 10, null)]), 0, null, null);

        Assert.True(plan.MissingPrices);
    }

    [Fact]
    public void A_cost_with_no_units_is_left_at_cost_not_written_off()
    {
        var holdings = RevaluationPlan.Combine(Costs(("OLD.ASX", 0.37m)), []);

        var h = Assert.Single(holdings);
        Assert.True(h.LeftAtCost);
        Assert.Equal(0m, h.Adjustment);
        Assert.Equal("OLD", h.StockCode);
        Assert.Equal("ASX", h.ExchangeCode);
    }

    [Fact]
    public void A_sold_holding_with_nil_cost_is_dropped()
    {
        Assert.Empty(RevaluationPlan.Combine(Costs(("GONE.ASX", 0m)), []));
    }

    [Fact]
    public void The_movement_takes_off_what_earlier_years_already_posted()
    {
        var plan = new RevaluationPlan(new DateOnly(2026, 6, 30),
            RevaluationPlan.Combine(Costs(("BHP.ASX", 30000m)), [Open("BHP", 1000, 45m)]), 12000m, null, null);

        Assert.Equal(15000m, plan.TargetAdjustment);
        Assert.Equal(3000m, plan.Movement);
    }

    [Theory]
    [InlineData(3000, 30, 900)]
    [InlineData(-3000, 30, -900)]
    [InlineData(1000.05, 10, 100.01)]
    [InlineData(5000, 0, 0)]
    public void Deferred_tax_is_the_rate_on_the_movement(decimal movement, decimal rate, decimal tax)
    {
        Assert.Equal(tax, RevaluationPlan.DeferredTax(movement, rate));
    }

    [Fact]
    public void The_journal_line_keeps_the_working()
    {
        var h = new RevaluationHolding("ASX", "BHP", 1000, 45m, 30000m);

        Assert.Equal("BHP.ASX: 1,000 units at 45.00 = 45,000.00 market value, less cost 30,000.00", h.Description);
    }

    [Fact]
    public void Companies_start_with_a_30_percent_deferred_tax_rate()
    {
        Assert.Equal(30m, RevaluationService.DefaultTaxRate(ClientEntityTypes.Company));
        Assert.Equal(0m, RevaluationService.DefaultTaxRate(ClientEntityTypes.Smsf));
    }

    [Fact]
    public void Revaluation_journals_have_a_plain_name()
    {
        Assert.Equal("Market value revaluation", JournalRules.SourceLabel("Revaluation"));
        Assert.False(JournalRules.IsManual("Revaluation"));
    }
}
