using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Tax;

namespace PortfolioManager.Tests;

public class RealisedGainTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    private static LedgerTrade Buy(int id, DateOnly date, decimal qty, decimal total, string code = "BHP") =>
        new(id, "ASX", code, "BUY", date, qty, total);

    private static LedgerTrade Sell(int id, DateOnly date, decimal qty, decimal total, string code = "BHP") =>
        new(id, "ASX", code, "SELL", date, qty, total);

    // FIFO cost of each sale

    [Fact]
    public void A_sale_takes_the_oldest_parcels_first()
    {
        var costs = SaleCosts.Match([
            Buy(1, D(2024, 1, 1), 100, 1000m),
            Buy(2, D(2024, 6, 1), 100, 1500m),
            Sell(3, D(2025, 1, 1), 150, 2700m),
        ]);

        Assert.Equal(1000m + 750m, costs[3]);
    }

    [Fact]
    public void Selling_everything_takes_the_whole_cost_with_no_rounding_left_behind()
    {
        // 1000 / 3 per part sale doesn't divide evenly; the last sale takes what's left
        var costs = SaleCosts.Match([
            Buy(1, D(2024, 1, 1), 3, 1000m),
            Sell(2, D(2025, 1, 1), 1, 400m),
            Sell(3, D(2025, 2, 1), 1, 400m),
            Sell(4, D(2025, 3, 1), 1, 400m),
        ]);

        Assert.Equal(333.33m, costs[2]);
        Assert.Equal(333.34m, costs[3]); // half of the 666.67 left
        Assert.Equal(333.33m, costs[4]);
        Assert.Equal(1000m, costs[2] + costs[3] + costs[4]);
    }

    [Fact]
    public void A_backdated_buy_changes_the_cost_of_a_later_sale()
    {
        LedgerTrade[] trades = [Buy(1, D(2024, 6, 1), 100, 2000m), Sell(2, D(2025, 1, 1), 100, 2500m)];
        Assert.Equal(2000m, SaleCosts.Match(trades)[2]);

        var withBackdated = trades.Append(Buy(3, D(2024, 1, 1), 100, 1000m));
        Assert.Equal(1000m, SaleCosts.Match(withBackdated)[2]);
    }

    [Fact]
    public void Buys_on_the_sale_date_count_and_sells_can_have_negative_quantities()
    {
        var costs = SaleCosts.Match([
            Buy(2, D(2025, 1, 1), 10, 100m),
            Sell(1, D(2025, 1, 1), -10, 120m),
        ]);

        Assert.Equal(100m, costs[1]);
    }

    [Fact]
    public void Selling_more_than_was_held_is_left_out()
    {
        var costs = SaleCosts.Match([Buy(1, D(2024, 1, 1), 10, 100m), Sell(2, D(2025, 1, 1), 20, 300m)]);

        Assert.False(costs.ContainsKey(2));
    }

    [Fact]
    public void Holdings_are_matched_separately_by_exchange_and_code()
    {
        var costs = SaleCosts.Match([
            Buy(1, D(2024, 1, 1), 10, 100m, "BHP"),
            Buy(2, D(2024, 1, 1), 10, 500m, "CBA"),
            Sell(3, D(2025, 1, 1), 10, 600m, "cba "),
        ]);

        Assert.Equal(500m, costs[3]);
    }

    [Fact]
    public void A_return_of_capital_reduces_the_cost_of_units_sold_later()
    {
        var costs = SaleCosts.Match(
            [Buy(1, D(2024, 1, 1), 100, 1000m), Sell(2, D(2025, 1, 1), 100, 1500m)],
            [new CapitalReturn("ASX", "BHP", D(2024, 6, 1), 200m)]);

        Assert.Equal(800m, costs[2]);
    }

    [Fact]
    public void A_return_of_capital_paid_on_the_sale_date_comes_after_the_sale()
    {
        var costs = SaleCosts.Match(
            [Buy(1, D(2024, 1, 1), 100, 1000m), Sell(2, D(2025, 1, 1), 50, 750m)],
            [new CapitalReturn("ASX", "BHP", D(2025, 1, 1), 100m)]);

        Assert.Equal(500m, costs[2]);
    }

    // Whether a sale's journal needs rewriting

    [Fact]
    public void An_unposted_sale_journal_needs_its_cost()
    {
        // As uspAddShareTrade posts it: proceeds credited to the cost base account
        var j = new SaleJournal(1, 10, 1, 1500m, 0m);

        Assert.True(j.NeedsPosting(1000m, out var cost));
        Assert.Equal(1000m, cost);
        Assert.Equal(1500m, j.Proceeds);
    }

    [Fact]
    public void A_journal_already_posting_the_cost_is_left_alone()
    {
        var j = new SaleJournal(1, 10, 1, 1000m, 500m);

        Assert.False(j.NeedsPosting(1000m, out _));
        Assert.Equal(1500m, j.Proceeds);
    }

    [Fact]
    public void A_loss_is_a_negative_gain_and_the_cost_changes_when_parcels_do()
    {
        var j = new SaleJournal(1, 10, 1, 1200m, -200m);

        Assert.False(j.NeedsPosting(1200m, out _));
        Assert.True(j.NeedsPosting(900m, out var cost));
        Assert.Equal(900m, cost);
    }

    [Fact]
    public void A_sale_with_no_fifo_cost_goes_back_to_proceeds_only_if_a_gain_was_posted()
    {
        Assert.False(new SaleJournal(1, 10, 1, 1500m, 0m).NeedsPosting(null, out _));

        Assert.True(new SaleJournal(1, 10, 1, 1000m, 500m).NeedsPosting(null, out var cost));
        Assert.Null(cost);
    }

    [Fact]
    public void A_journal_without_one_cost_base_line_is_never_touched()
    {
        Assert.False(new SaleJournal(1, 10, 0, 0m, 0m).NeedsPosting(1000m, out _));
        Assert.False(new SaleJournal(1, 10, 2, 1500m, 0m).NeedsPosting(1000m, out _));
    }

    [Fact]
    public void Sale_journal_rows_map_from_the_proc()
    {
        var rows = new List<IDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["iShareTradeId"] = 7, ["iJournalID"] = 70, ["CostBaseLines"] = 1, ["CostPosted"] = 1000.5m, ["GainPosted"] = -20m },
            new Dictionary<string, object?> { ["iShareTradeId"] = 8, ["iJournalID"] = DBNull.Value },
        };

        var j = Assert.Single(RealisedGainService.MapSaleJournals(rows));
        Assert.Equal(new SaleJournal(7, 70, 1, 1000.5m, -20m), j);
    }

    [Theory]
    [InlineData(2050, 0.657895, 3116.00)]   // US$2,050 at 0.657895 US$ per A$
    [InlineData(4000, 1, 4000)]
    [InlineData(4000, 0, 4000)]             // no rate recorded: left as it is
    public void Ledger_amount_is_in_the_portfolio_currency(decimal orderTotal, decimal rate, decimal expected) =>
        Assert.Equal(expected, LedgerTrade.InPortfolioCurrency(orderTotal, rate));
}
