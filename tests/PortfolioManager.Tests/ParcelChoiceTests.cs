using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Tax;

namespace PortfolioManager.Tests;

public class ParcelChoiceTests
{
    // Three parcels of CBA: cheap and old, dear and old, middling and recent; then a sale of 100 at $10 net
    private static readonly TradeLot[] Trades =
    [
        new(1, "ASX", "CBA", "Buy", new DateOnly(2020, 1, 1), 100, 5m),
        new(2, "ASX", "CBA", "Buy", new DateOnly(2021, 1, 1), 100, 12m),
        new(3, "ASX", "CBA", "Buy", new DateOnly(2026, 6, 1), 100, 9m),
        new(4, "ASX", "CBA", "Sell", new DateOnly(2026, 9, 1), 100, 10m),
    ];

    private static readonly LedgerTrade[] Ledger =
    [
        new(1, "ASX", "CBA", "Buy", new DateOnly(2020, 1, 1), 100, 500m),
        new(2, "ASX", "CBA", "Buy", new DateOnly(2021, 1, 1), 100, 1200m),
        new(3, "ASX", "CBA", "Buy", new DateOnly(2026, 6, 1), 100, 900m),
        new(4, "ASX", "CBA", "Sell", new DateOnly(2026, 9, 1), 100, 1000m),
    ];

    private static Dictionary<int, SaleParcelChoice> Choice(params ParcelPick[] picks) =>
        new() { [4] = new SaleParcelChoice(4, ParcelMethod.Specific, picks) };

    [Fact]
    public void No_choice_is_first_in_first_out()
    {
        var e = Assert.Single(CgtParcels.Match(Trades).Events);
        Assert.Equal((new DateOnly(2020, 1, 1), 500m, 1), (e.Acquired, e.Gain, e.BuyTradeId));
        Assert.Equal(4, e.SellTradeId);
    }

    [Fact]
    public void Specific_parcels_are_taken_and_the_rest_stay_open()
    {
        var match = CgtParcels.Match(Trades, null, null, Choice(new ParcelPick(2, 60), new ParcelPick(3, 40)));

        Assert.Equal([(2, 60m, -120m), (3, 40m, 40m)], match.Events.Select(e => (e.BuyTradeId, e.Units, e.Gain)));
        Assert.Equal([(1, 100m), (2, 40m), (3, 60m)], match.Open.Select(p => (p.BuyTradeId, p.Units)));
    }

    [Fact]
    public void The_ledger_cost_follows_the_same_parcels()
    {
        var choice = Choice(new ParcelPick(2, 60), new ParcelPick(3, 40));
        var cgtCost = CgtParcels.Match(Trades, null, null, choice).Events.Sum(e => e.CostBase);
        Assert.Equal(1080m, cgtCost);
        Assert.Equal(cgtCost, SaleCosts.Match(Ledger, null, choice)[4]);
        Assert.Equal(500m, SaleCosts.Match(Ledger)[4]);
    }

    [Fact]
    public void A_pick_that_no_longer_fits_is_topped_up_first_in_first_out()
    {
        // Parcel 9 doesn't exist (deleted since); parcel 2 is asked for 80 but the sale is 100, so 20 more come first in, first out
        var match = CgtParcels.Match(Trades, null, null, Choice(new ParcelPick(9, 50), new ParcelPick(2, 80)));
        Assert.Equal([(1, 20m), (2, 80m)], match.Events.Select(e => (e.BuyTradeId, e.Units)));
        Assert.Empty(match.Problems);
        Assert.True(ParcelChoices.IsStale([(1, 100m), (2, 100m), (3, 100m)], 100, Choice(new ParcelPick(9, 50), new ParcelPick(2, 150))[4]));
    }

    [Fact]
    public void Smallest_gain_takes_the_loss_then_the_recent_parcel()
    {
        HeldParcel[] held = [new(1, new DateOnly(2020, 1, 1), 100, 5m), new(2, new DateOnly(2021, 1, 1), 100, 12m), new(3, new DateOnly(2026, 6, 1), 100, 9m)];

        var min = ParcelChoices.Plan(ParcelMethod.MinGain, held, 150, new DateOnly(2026, 9, 1), 10m, 0.5m);
        Assert.Equal([new ParcelPick(2, 100), new ParcelPick(3, 50)], min);

        // Parcel 1's $5 gain is discounted to $2.50, parcel 3's $1 isn't; largest takes parcel 1 first
        var max = ParcelChoices.Plan(ParcelMethod.MaxGain, held, 150, new DateOnly(2026, 9, 1), 10m, 0.5m);
        Assert.Equal([new ParcelPick(1, 100), new ParcelPick(3, 50)], max);
    }

    [Fact]
    public void The_discount_can_change_which_gain_is_smaller()
    {
        // $3 gain discounted (1.50) beats a $2 gain that isn't
        HeldParcel[] held = [new(1, new DateOnly(2020, 1, 1), 10, 7m), new(2, new DateOnly(2026, 6, 1), 10, 8m)];
        Assert.Equal(1, ParcelChoices.Plan(ParcelMethod.MinGain, held, 10, new DateOnly(2026, 9, 1), 10m, 0.5m)[0].BuyTradeId);
        // With no discount (a company) the $2 gain is smaller
        Assert.Equal(2, ParcelChoices.Plan(ParcelMethod.MinGain, held, 10, new DateOnly(2026, 9, 1), 10m, 0m)[0].BuyTradeId);
    }

    [Fact]
    public void Picks_are_checked_before_saving()
    {
        var sale = new SaleParcels(
            new PortfolioManager.Core.Trades.ShareTrade(4, new DateTime(2026, 9, 1), "Sell", "ASX", "CBA", "", 100, 10, 0, "AUD", 1, 1000, 10, ""),
            100, 10, [new(1, new DateOnly(2020, 1, 1), 100, 5m), new(2, new DateOnly(2021, 1, 1), 50, 12m)], null, [], false);

        Assert.Null(ParcelChoiceService.Validate(sale, [new(1, 50), new(2, 50)]));
        Assert.Contains("add up", ParcelChoiceService.Validate(sale, [new(1, 50)]));
        Assert.Contains("Only 50", ParcelChoiceService.Validate(sale, [new(2, 100)]));
        Assert.Contains("wasn't held", ParcelChoiceService.Validate(sale, [new(7, 100)]));
        Assert.Contains("at least one", ParcelChoiceService.Validate(sale, []));
    }

    [Fact]
    public void Picks_go_to_the_proc_as_xml()
    {
        Assert.Equal("<picks><p b=\"2\" u=\"60.5\" /></picks>", ParcelChoiceService.PicksXml([new(2, 60.5m), new(3, 0)]));
    }

    [Fact]
    public void Rows_from_the_proc_are_grouped_by_sale()
    {
        var choices = ParcelChoiceService.Group([(4, "MinGain", 2, 60), (4, "MinGain", 3, 40), (8, "Specific", null, null)]);
        Assert.Equal(ParcelMethod.MinGain, choices[4].Method);
        Assert.Equal(2, choices[4].Picks.Count);
        Assert.Empty(choices[8].Picks);
    }
}
