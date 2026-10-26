using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.CorporateActions;
using PortfolioManager.Core.Tax;

namespace PortfolioManager.Tests;

public class ParcelTransferTests
{
    private static readonly DateOnly Today = new(2026, 10, 21);

    // Two parcels of OLD ($1,000 and $3,000), then a demerger: 1 NEW for every 2 OLD, 25% of the cost base to NEW
    private static readonly TradeLot[] DemergerTrades =
    [
        new(1, "ASX", "OLD", "Buy", new DateOnly(2020, 1, 1), 100, 10m),
        new(2, "ASX", "OLD", "Buy", new DateOnly(2026, 1, 1), 100, 30m),
        new(3, "ASX", "AAA", "Buy", new DateOnly(2026, 3, 1), 100, 10m),   // the demerger's purchase, at the cost moved
        new(4, "ASX", "AAA", "Sell", new DateOnly(2026, 9, 1), 100, 20m),
    ];

    private static CapitalReturn Demerger(decimal share = 0.25m) =>
        new("ASX", "OLD", new DateOnly(2026, 3, 1), 0m, Transfer: new ParcelTransfer(ParcelTransfer.Demerger, "ASX", "AAA", 0.5m, share, BuyTradeId: 3));

    [Fact]
    public void Demerger_moves_the_cost_share_and_keeps_purchase_dates()
    {
        var m = CgtParcels.Match(DemergerTrades, null, [Demerger()]);

        Assert.Empty(m.Problems);
        Assert.Equal(0, m.CapitalReturns);
        // The new shares were sold: two parcels, dated from the old purchases, with 25% of each cost base
        var sold = m.Events.Where(e => e.StockCode == "AAA").OrderBy(e => e.Acquired).ToList();
        Assert.Equal(2, sold.Count);
        Assert.Equal((new DateOnly(2020, 1, 1), 50m, 250m, 1000m), (sold[0].Acquired, sold[0].Units, sold[0].CostBase, sold[0].Proceeds));
        Assert.Equal((new DateOnly(2026, 1, 1), 50m, 750m, 1000m), (sold[1].Acquired, sold[1].Units, sold[1].CostBase, sold[1].Proceeds));
        Assert.True(sold[0].DiscountEligible);
        Assert.False(sold[1].DiscountEligible);
        // The old shares keep 75%
        var old = m.Open.Where(p => p.StockCode == "OLD").ToList();
        Assert.Equal([750m, 2250m], old.Select(p => p.TotalCost));
    }

    [Fact]
    public void Demerger_new_units_are_spread_to_the_purchase_quantity()
    {
        var trades = DemergerTrades.Take(2).Append(new TradeLot(3, "ASX", "AAA", "Buy", new DateOnly(2026, 3, 1), 33, 1m)).ToList();
        var open = CgtParcels.Match(trades, null, [Demerger()]).Open.Where(p => p.StockCode == "AAA").ToList();

        Assert.Equal(33m, open.Sum(p => p.Units));
        Assert.Equal(1000m, open.Sum(p => p.TotalCost));
    }

    [Fact]
    public void Demerger_before_the_cut_off_is_left_out_with_its_purchase()
    {
        var m = CgtParcels.Match(DemergerTrades, new DateOnly(2026, 2, 1), [Demerger()]);
        Assert.Equal(4000m, m.Open.Sum(p => p.TotalCost));
    }

    [Fact]
    public void Rollover_takeover_counts_only_the_cash_and_moves_the_rest()
    {
        // 200 TGT (cost $4,000) given up for $1,000 cash and 100 BUYR worth $3,000: 75% of the cost base rolls over
        TradeLot[] trades =
        [
            new(1, "ASX", "TGT", "Buy", new DateOnly(2020, 1, 1), 200, 20m),
            new(2, "ASX", "TGT", "Sell", new DateOnly(2026, 8, 1), 200, 20m),
            new(3, "ASX", "BUYR", "Buy", new DateOnly(2026, 8, 1), 100, 30m),
        ];
        var lot = new CapitalReturn("ASX", "TGT", new DateOnly(2026, 8, 1), 0m,
            Transfer: new ParcelTransfer(ParcelTransfer.Takeover, "ASX", "BUYR", 0.5m, 0.75m, 3000m, BuyTradeId: 3, SellTradeId: 2));
        var m = CgtParcels.Match(trades, null, [lot]);

        var e = Assert.Single(m.Events);
        Assert.Equal((1000m, 1000m, 0m), (e.CostBase, e.Proceeds, e.Gain));
        var p = Assert.Single(m.Open);
        Assert.Equal(("BUYR", new DateOnly(2020, 1, 1), 100m, 3000m), (p.StockCode, p.Acquired, p.Units, p.TotalCost));
    }

    [Fact]
    public void Full_scrip_rollover_has_no_event()
    {
        TradeLot[] trades =
        [
            new(1, "ASX", "TGT", "Buy", new DateOnly(2020, 1, 1), 200, 20m),
            new(2, "ASX", "TGT", "Sell", new DateOnly(2026, 8, 1), 200, 25m),
            new(3, "ASX", "BUYR", "Buy", new DateOnly(2026, 8, 1), 100, 50m),
        ];
        var lot = new CapitalReturn("ASX", "TGT", new DateOnly(2026, 8, 1), 0m,
            Transfer: new ParcelTransfer(ParcelTransfer.Takeover, "ASX", "BUYR", 0.5m, 1m, 5000m, BuyTradeId: 3, SellTradeId: 2));
        var m = CgtParcels.Match(trades, null, [lot]);

        Assert.Empty(m.Events);
        Assert.Equal(4000m, Assert.Single(m.Open).TotalCost);
    }

    [Fact]
    public void Ledger_cost_follows_the_demerger_journal()
    {
        LedgerTrade[] trades =
        [
            new(1, "ASX", "OLD", "Buy", new DateOnly(2020, 1, 1), 100, 4000m),
            new(2, "ASX", "OLD", "Sell", new DateOnly(2026, 9, 1), 100, 5000m),
        ];
        Assert.Equal(3000m, SaleCosts.Match(trades, [Demerger()])[2]);
    }

    [Fact]
    public void Record_gives_a_lot_only_for_demergers_and_rollovers()
    {
        ParcelTransferRecord R(string kind) => new(1, kind, new DateTime(2026, 3, 1), "ASX", "OLD", "ASX", "NEW", 1, 0.2, 0, 5, 6, null);
        Assert.NotNull(R("Demerger").ToLot());
        Assert.NotNull(R("Takeover").ToLot());
        Assert.Null(R("TakeoverTaxed").ToLot());
        Assert.Null(R("CodeChange").ToLot());
    }

    [Fact]
    public void Validate_demerger_needs_the_new_code_units_and_percentage()
    {
        var r = ParcelTransferEntry.Validate(new TransferForm { Kind = "Demerger", Market = "asx", Code = "old", Date = new DateOnly(2026, 3, 1) }, Today);
        Assert.Contains(r.Errors, e => e.Field == nameof(TransferForm.NewCode));
        Assert.Contains(r.Errors, e => e.Field == nameof(TransferForm.NewUnits));
        Assert.Contains(r.Errors, e => e.Field == nameof(TransferForm.CostSharePercent));

        var ok = ParcelTransferEntry.Validate(new TransferForm
        {
            Kind = "demerger", Market = "asx", Code = "old", Date = new DateOnly(2026, 3, 1), NewCode = "aaa", NewUnits = 50, CostSharePercent = 25,
        }, Today);
        Assert.True(ok.IsValid);
        Assert.Equal(("Demerger", "ASX", "AAA", 0.25m), (ok.Value!.Kind, ok.Value.NewMarket, ok.Value.NewCode, ok.Value.CostShare));
    }

    [Fact]
    public void Validate_cash_only_takeover_needs_no_new_shares()
    {
        var r = ParcelTransferEntry.Validate(new TransferForm
        {
            Kind = "Takeover", Market = "ASX", Code = "TGT", Date = new DateOnly(2026, 8, 1), CashPerShare = 5, Rollover = true,
        }, Today);
        Assert.True(r.IsValid);
        Assert.False(r.Value!.Rollover);
        Assert.NotEmpty(ParcelTransferEntry.Validate(new TransferForm { Kind = "Takeover", Market = "ASX", Code = "TGT", Date = new DateOnly(2026, 8, 1) }, Today).Errors);
        Assert.NotEmpty(ParcelTransferEntry.Validate(new TransferForm { Kind = "Merger", Market = "ASX", Code = "TGT", Date = new DateOnly(2026, 8, 1) }, Today).Errors);
    }

    [Fact]
    public void Preview_takeover_works_out_the_rollover_share()
    {
        OpenParcel[] open = [new("ASX", "TGT", new DateOnly(2020, 1, 1), 150, 20m), new("ASX", "TGT", new DateOnly(2024, 1, 1), 50, 20m), new("ASX", "XYZ", new DateOnly(2020, 1, 1), 5, 1m)];
        var t = new NewParcelTransfer("Takeover", "ASX", "TGT", new DateOnly(2026, 8, 1), "ASX", "BUYR", null, 100, null, 5m, 30m, true, null);
        var p = ParcelTransferEntry.Preview(open, t);

        Assert.Equal((200m, 4000m, 1000m, 3000m, 0.75m), (p.UnitsHeld, p.CostBase, p.Cash, p.ScripValue, p.CostShare));
        Assert.Equal(3000m, p.CostMoved);
        Assert.Equal([75m, 25m], p.Parcels.Select(x => x.NewUnits));
        Assert.Equal(0m, p.GainNow(true));
        Assert.Equal(0m, p.GainNow(false));
        Assert.Equal(0.5m, p.NewPerOld(100));
    }

    [Fact]
    public void Partial_takeover_takes_the_oldest_parcels_and_matches_the_sale_the_same_way()
    {
        OpenParcel[] open = [new("ASX", "TGT", new DateOnly(2020, 1, 1), 150, 20m), new("ASX", "TGT", new DateOnly(2024, 1, 1), 50, 40m)];
        var form = new TransferForm
        {
            Kind = "Takeover", Market = "ASX", Code = "TGT", Date = new DateOnly(2026, 8, 1), Units = 160, CashPerShare = 25,
        };
        var t = ParcelTransferEntry.Validate(form, Today).Value!;
        Assert.Equal(160m, t.Units);
        var p = ParcelTransferEntry.Preview(open, t);
        // 150 from the 2020 parcel and 10 from the 2024 one
        Assert.Equal((160m, 3400m, 4000m), (p.UnitsHeld, p.CostBase, p.Cash));
        Assert.Equal(600m, p.GainNow(false));

        // The sale it records is matched first in, first out too, so the gain agrees and 40 units stay
        TradeLot[] trades =
        [
            new(1, "ASX", "TGT", "Buy", new DateOnly(2020, 1, 1), 150, 20m),
            new(2, "ASX", "TGT", "Buy", new DateOnly(2024, 1, 1), 50, 40m),
            new(3, "ASX", "TGT", "Sell", new DateOnly(2026, 8, 1), 160, 25m),
        ];
        var m = CgtParcels.Match(trades);
        Assert.Equal(600m, m.Events.Sum(e => e.Gain));
        Assert.Equal(40m, Assert.Single(m.Open).Units);

        Assert.NotEmpty(ParcelTransferEntry.Validate(new TransferForm
        {
            Kind = "Takeover", Market = "ASX", Code = "TGT", Date = new DateOnly(2026, 8, 1), Units = -1, CashPerShare = 25,
        }, Today).Errors);
    }

    [Fact]
    public void Demerger_with_a_dividend_part_works_out_the_dividend_and_franking()
    {
        OpenParcel[] open = [new("ASX", "OLD", new DateOnly(2020, 1, 1), 1000, 10m)];
        var t = ParcelTransferEntry.Validate(new TransferForm
        {
            Kind = "Demerger", Market = "ASX", Code = "OLD", Date = new DateOnly(2026, 3, 1), NewCode = "NEW", NewUnits = 250,
            CostSharePercent = 20, DividendPerShare = 0.21m, DividendFrankedPercent = 100,
        }, Today).Value!;
        var p = ParcelTransferEntry.Preview(open, t);

        Assert.Equal((210m, 210m, 0m, 90m), (p.Dividend, p.DividendFranked, p.DividendUnfranked, p.DividendFrankingCredit));
        Assert.Equal(2000m, p.CostMoved); // the dividend doesn't change the cost base split

        var args = ParcelTransferService.DemergerDividendParameters(7, t, p, 55);
        var values = args.GetType().GetProperties().ToDictionary(x => x.Name, x => x.GetValue(args));
        Assert.Equal(("OLD", 210d, 210d, 0d, 90d, 55), ((string)values["strStockCode"]!, (double)values["dblTotal"]!, (double)values["dblFrankedAmount"]!,
            (double)values["dblUnFrankedAmount"]!, (double)values["dblFrankingCredit"]!, (int?)values["iTargetAccountID"]));

        Assert.NotEmpty(ParcelTransferEntry.Validate(new TransferForm
        {
            Kind = "Demerger", Market = "ASX", Code = "OLD", Date = new DateOnly(2026, 3, 1), NewCode = "NEW", NewUnits = 250,
            CostSharePercent = 20, DividendPerShare = 0.21m, DividendFrankedPercent = 120,
        }, Today).Errors);
    }

    [Fact]
    public void Part_units_are_allowed_for_new_shares()
    {
        var ok = ParcelTransferEntry.Validate(new TransferForm
        {
            Kind = "Demerger", Market = "ASX", Code = "OLD", Date = new DateOnly(2026, 3, 1), NewCode = "NEW", NewUnits = 12.5m, CostSharePercent = 10,
        }, Today);
        Assert.True(ok.IsValid);
        Assert.Equal(12.5m, ok.Value!.NewUnits);
    }
}
