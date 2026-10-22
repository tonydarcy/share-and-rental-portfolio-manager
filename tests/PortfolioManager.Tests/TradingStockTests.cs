using PortfolioManager.Core.Clients;
using PortfolioManager.Core.Tax;
using PortfolioManager.Core.TaxWork;
using static PortfolioManager.Tests.TaxReconciliationTests;

namespace PortfolioManager.Tests;

public class TradingStockTests
{
    private static readonly Dictionary<(string, string), decimal> NoPrices = [];
    private static readonly DateOnly From = new(2025, 7, 1), To = new(2026, 6, 30);

    // Held 100 AAA from before the year ($1,000); bought 100 BBB ($2,000) and sold 50 of them for $1,500
    private static readonly TradeLot[] Trades =
    [
        new(1, "ASX", "AAA", "Buy", new DateOnly(2024, 1, 1), 100, 10m),
        new(2, "ASX", "BBB", "Buy", new DateOnly(2025, 8, 1), 100, 20m),
        new(3, "ASX", "BBB", "Sell", new DateOnly(2026, 3, 1), 50, 30m),
    ];

    private static TradingStockReport Run(string valuation, int fromYear = 2026, Dictionary<(string, string), decimal>? opening = null)
    {
        var settings = new ShareTraderSettings(fromYear, valuation);
        var close = new Dictionary<(string, string), decimal> { [("ASX", "AAA")] = 8m, [("ASX", "BBB")] = 25m };
        return TradingStock.Build(2026, From, To, settings, Trades, new HashSet<int>(),
            CgtParcels.Match(Trades, From.AddDays(-1)).Open, CgtParcels.Match(Trades, To).Open, opening ?? [], close);
    }

    [Fact]
    public void At_cost_the_profit_is_the_realised_gain()
    {
        var r = Run(TradingStockValuation.Cost);
        Assert.True(r.FirstYear);
        Assert.Equal((1000m, 2000m, 1500m, 2000m, 500m), (r.OpeningValue, r.Purchases, r.Sales, r.ClosingValue, r.Profit));
    }

    [Fact]
    public void At_market_value_the_unrealised_change_counts_too()
    {
        // Closing AAA 100 x $8 = $800, BBB 50 x $25 = $1,250; opening at cost in the first year
        var r = Run(TradingStockValuation.Market);
        Assert.Equal((2050m, 2050m + 1500m - 1000m - 2000m), (r.ClosingValue, r.Profit));
    }

    [Fact]
    public void Lower_of_cost_and_market_takes_each_holding_separately()
    {
        var r = Run(TradingStockValuation.Lower);
        Assert.Equal(800m, r.Lines.Single(l => l.StockCode == "AAA").ClosingValue);
        Assert.Equal(1000m, r.Lines.Single(l => l.StockCode == "BBB").ClosingValue);
    }

    [Fact]
    public void After_the_first_year_opening_stock_uses_the_same_method_and_a_missing_price_falls_back_to_cost()
    {
        var r = Run(TradingStockValuation.Market, 2025, new() { [("ASX", "AAA")] = 9m });
        Assert.False(r.FirstYear);
        Assert.Equal(900m, r.OpeningValue);
        var none = TradingStock.Build(2026, From, To, new(2025, TradingStockValuation.Market), Trades, new HashSet<int>(),
            CgtParcels.Match(Trades, From.AddDays(-1)).Open, CgtParcels.Match(Trades, To).Open, NoPrices, NoPrices);
        Assert.Equal(3, none.Problems.Count(p => p.Contains("valued at cost")));   // AAA at the start, AAA and BBB at the end
    }

    [Fact]
    public void A_demerger_purchase_isnt_a_purchase()
    {
        TradeLot[] trades = [.. Trades, new(4, "ASX", "NEW", "Buy", new DateOnly(2026, 1, 1), 10, 5m)];
        var r = TradingStock.Build(2026, From, To, new(2026, TradingStockValuation.Cost), trades, new HashSet<int> { 4 },
            [], CgtParcels.Match(trades, To).Open, NoPrices, NoPrices);
        Assert.Equal(0m, r.Lines.Single(l => l.StockCode == "NEW").Purchases);
    }

    [Fact]
    public void Settings_apply_from_the_first_year_on()
    {
        Assert.True(new ShareTraderSettings(2026, "Cost").IsTraderIn(2027));
        Assert.False(new ShareTraderSettings(2026, "Cost").IsTraderIn(2025));
        Assert.False(ShareTraderSettings.None.IsTraderIn(2026));
    }

    [Fact]
    public void The_workpaper_counts_trading_profit_instead_of_capital_gains()
    {
        var events = new List<CgtEvent> { new("ASX", "BHP", new(2020, 1, 1), new(2026, 3, 1), 100, 1000, 3000) };
        var report = new CgtReturnReport(CgtDiscount.Summarise(events, HalfRate), events, true, []);
        var trading = Run(TradingStockValuation.Cost);
        var w = TaxWorkpaperService.Assemble(ClientDetails.Unsaved(1, "Co", "Company"), 2026, Year(), TaxYearData.Empty, report, TaxIncomeFacts.None, trading: trading);

        Assert.Equal(0m, w.Cgt.NetCapitalGain);
        Assert.Contains(w.Reconciliation.Lines, l => l.Label.Contains("trading stock") && l.Amount == 500m);
        Assert.Equal(500m, w.Reconciliation.TaxableIncome);

        var person = TaxWorkpaperService.Assemble(ClientDetails.Unsaved(1, "Jo", "Individual"), 2026, Year(), TaxYearData.Empty, report, TaxIncomeFacts.None, trading: trading);
        var labels = TaxReturnSchedule.For(person).Labels.ToDictionary(l => l.Code, l => l.Amount);
        Assert.Equal(500m, labels["P8"]);
        Assert.False(labels.ContainsKey("18H"));
    }
}
