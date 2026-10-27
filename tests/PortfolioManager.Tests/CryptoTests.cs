using System.Text.Json;
using PortfolioManager.Core.Crypto;
using PortfolioManager.Core.Import;
using PortfolioManager.Core.Imports;
using PortfolioManager.Core.NavexaSync;
using PortfolioManager.Core.Payouts;
using PortfolioManager.Core.Trades;

namespace PortfolioManager.Tests;

public class CryptoTests
{
    private static readonly DateOnly Today = new(2026, 10, 27);

    // ---------- Codes and franking ----------

    [Theory]
    [InlineData("BTC", "AUD", "BTC-AUD")]
    [InlineData(" eth ", "AUD", "ETH-AUD")]
    [InlineData("BTC/USD", "AUD", "BTC-USD")]
    [InlineData("ETH-AUD", "USD", "ETH-AUD")]
    public void Coins_get_the_price_feeds_code(string coin, string currency, string expected) =>
        Assert.Equal(expected, CryptoCodes.SiteCode(coin, currency));

    [Fact]
    public void Crypto_never_gets_franking()
    {
        Assert.NotNull(CryptoCodes.CheckNoFranking("CRYPTO", 10, 0));
        Assert.NotNull(CryptoCodes.CheckNoFranking("crypto", 0, 4.29m));
        Assert.Null(CryptoCodes.CheckNoFranking("CRYPTO", 0, 0));
        Assert.Null(CryptoCodes.CheckNoFranking("ASX", 70, 30));

        var payout = new PayoutInput { StockExchangeCode = "CRYPTO", StockCode = "ETH-AUD", Total = 10, Franked = 10, FrankingCredit = 4.29m };
        Assert.Contains(payout.Validate(), e => e.Contains("franked"));
        var drp = new DrpInput { StockExchangeCode = "CRYPTO", StockCode = "ETH-AUD", Date = new(2025, 1, 1), Franked = 10, Units = 1, Price = 10 };
        Assert.Contains(drp.Validate(), e => e.Contains("franked"));
        var file = DividendDetailsCsv.Parse("Exchange,Code,Ex date,Franked %,DRP price\nCRYPTO,BTC-AUD,2026-08-20,100,\n");
        Assert.Contains(file.Lines.Single().Errors, e => e.Contains("Crypto"));
    }

    // ---------- Swaps and rewards ----------

    [Fact]
    public void A_swap_is_a_sale_and_a_purchase_at_the_same_value()
    {
        var form = new SwapForm { Date = new(2025, 4, 1), FromCoin = "btc", FromUnits = 0.005m, ToCoin = "ETH", ToUnits = 0.12m, Value = 500m };
        var result = CryptoEntry.Validate(form, "AUD", Today);
        Assert.True(result.IsValid);
        var trades = CryptoService.SwapTrades(result.Value!);
        Assert.Equal(TradeType.Sell, trades[0].Type);
        Assert.Equal("BTC-AUD", trades[0].Code);
        Assert.Equal(TradeType.Buy, trades[1].Type);
        Assert.Equal("ETH-AUD", trades[1].Code);
        Assert.All(trades, t => Assert.Equal("CRYPTO", t.Market));
        Assert.All(trades, t => Assert.Null(t.Cash));
        Assert.All(trades, t => Assert.Equal(500m, Math.Round(t.Quantity * t.Price, 2)));
        Assert.All(trades, t => Assert.Equal(0m, t.Brokerage));
    }

    [Fact]
    public void A_swap_needs_two_different_coins_and_a_value()
    {
        var result = CryptoEntry.Validate(new SwapForm { Date = new(2025, 4, 1), FromCoin = "BTC", FromUnits = 1, ToCoin = "btc-aud", ToUnits = 1 }, "AUD", Today);
        Assert.Contains(result.Errors, e => e.Field == nameof(SwapForm.ToCoin));
        Assert.Contains(result.Errors, e => e.Field == nameof(SwapForm.Value));
    }

    [Fact]
    public void A_reward_is_other_income_paid_in_coins_at_its_value()
    {
        var result = CryptoEntry.Validate(new RewardForm { Date = new(2025, 5, 1), Coin = "ETH", Units = 0.001m, Value = 4.5m, Kind = "staking" }, "AUD", Today);
        Assert.True(result.IsValid);
        var p = CryptoService.RewardParameters(7, result.Value!);
        Assert.Equal("CRYPTO", p["strStockExchangeCode"]);
        Assert.Equal("ETH-AUD", p["strStockCode"]);
        Assert.Equal(4.5d, p["dblTotalPayout"]);
        Assert.Equal(4.5d, p["dblOtherAmount"]);
        Assert.Equal(0d, p["dblFrankedAmount"]);
        Assert.Equal(0d, p["dblUnFrankedAmount"]);
        Assert.Equal(0d, p["dblFrankingCredit"]);
        Assert.Equal(4500d, (double)p["dblPricePerUnit"]!, 6);
        Assert.Equal("Crypto staking reward", p["strComment"]);
    }

    // ---------- Navexa sync ----------

    private static NavexaApiTransaction Tx(string json) => JsonSerializer.Deserialize<NavexaApiTransaction>(json, NavexaApiClient.Json)!;

    private static SyncPlan Plan(params NavexaApiTransaction[] items) =>
        NavexaSyncPlanner.Plan(items, new SyncSettings("AUD", null, null, new Dictionary<string, HoldingMapping?>(), new Dictionary<string, string?>(), new Dictionary<string, int?>()),
            new Dictionary<string, KnownItem>(), ExistingRecords.None);

    // Shapes copied from a real Navexa crypto record (ids and amounts changed)
    private const string BtcBuy = """
        {"id":"179537","type":"cryptoTrade","date":"2025-04-07T00:00:00Z","tradeTypeId":1,"tradeType":"buy","value":810.44,"currency":"AUD","baseValue":810.44,"baseCurrency":"AUD",
         "incoming":[{"id":"179537","holding":{"id":562693,"symbol":"BTC","name":"Bitcoin","exchange":"Crypto","displayExchange":"Crypto","holdingTypeId":2,"holdingType":"Crypto"},"currency":"AUD","quantity":0.01243653,"price":65166.09,"amount":810.44,"baseAmount":810.44,"tradeTypeId":1,"tradeType":"buy"}],
         "outgoing":[{"currency":"AUD","quantity":810.44,"price":1,"amount":810.44,"baseAmount":810.44}],
         "fees":[{"currency":"AUD","quantity":0.18,"price":0,"amount":0.18,"baseAmount":0.18}],
         "notes":"Import from BTC Markets - BTC/AUD","isConfirmed":false,"autoAdded":false,"lastModifiedDate":"2025-04-07T09:53:55.3497093+10:00"}
        """;

    [Fact]
    public void Navexa_crypto_buys_come_across_on_market_crypto()
    {
        var item = Plan(Tx(BtcBuy)).Items.Single();
        Assert.Equal(SyncStatus.Ready, item.Status);
        Assert.Equal(SyncKind.Trade, item.Kind);
        var t = item.Trade!;
        Assert.Equal(("CRYPTO", "BTC-AUD", "Buy"), (t.Exchange, t.Code, t.Type));
        Assert.Equal(0.01243653m, t.Quantity);
        Assert.Equal(65166.09m, t.Price);
        Assert.Equal(0.18m, t.Brokerage);
        Assert.Null(t.BankAccount);
    }

    [Fact]
    public void A_dust_sale_with_a_bigger_fee_is_cut_to_nil()
    {
        var item = Plan(Tx("""
            {"id":"298812","type":"cryptoTrade","date":"2025-10-08T00:00:00Z","tradeTypeId":2,"tradeType":"sell","value":0.0078,"currency":"AUD",
             "incoming":[{"currency":"AUD","quantity":0.0078,"price":1,"amount":0.0078}],
             "outgoing":[{"id":"298812","holding":{"id":562731,"symbol":"COMP","exchange":"Crypto","holdingType":"Crypto"},"currency":"AUD","quantity":0.011171992,"price":0.7,"amount":0.0078,"tradeTypeId":2}],
             "fees":[{"currency":"AUD","quantity":0.01,"price":0,"amount":0.01}]}
            """)).Items.Single();
        Assert.Equal(SyncStatus.Ready, item.Status);
        Assert.Equal("COMP-AUD", item.Trade!.Code);
        Assert.Equal(0m, item.Trade.Brokerage);
        Assert.Contains(item.Warnings, w => w.Contains("more than the sale proceeds"));
    }

    [Fact]
    public void A_fee_paid_in_coins_is_flagged()
    {
        var item = Plan(Tx("""
            {"id":"1","type":"cryptoTrade","date":"2024-05-16T00:00:00Z","tradeTypeId":1,"currency":"AUD",
             "incoming":[{"holding":{"id":562693,"symbol":"BTC","exchange":"Crypto","holdingType":"Crypto"},"currency":"AUD","quantity":0.001,"price":98000}],
             "fees":[{"holding":{"id":562693,"symbol":"BTC","exchange":"Crypto","holdingType":"Crypto"},"currency":"AUD","quantity":0.00001,"price":98000,"amount":0.98,"tradeTypeId":202}]}
            """)).Items.Single();
        Assert.Equal(0.98m, item.Trade!.Brokerage);
        Assert.Contains(item.Warnings, w => w.Contains("paid in coins"));
    }

    [Fact]
    public void A_navexa_swap_is_a_sale_and_a_purchase_at_its_value()
    {
        var item = Plan(Tx("""
            {"id":"9","type":"cryptoTrade","date":"2025-01-10T00:00:00Z","tradeTypeId":7,"tradeType":"trade","value":1500,"currency":"AUD","baseValue":1500,"baseCurrency":"AUD",
             "incoming":[{"holding":{"id":2,"symbol":"ETH","exchange":"Crypto","holdingType":"Crypto"},"currency":"AUD","quantity":0.3,"price":5000,"amount":1500,"baseAmount":1500}],
             "outgoing":[{"holding":{"id":1,"symbol":"BTC","exchange":"Crypto","holdingType":"Crypto"},"currency":"AUD","quantity":0.01,"price":150000,"amount":1500,"baseAmount":1500}]}
            """)).Items.Single();
        Assert.Equal(SyncStatus.Ready, item.Status);
        Assert.Equal(SyncKind.Swap, item.Kind);
        Assert.Equal(("BTC-AUD", 0.01m, "ETH-AUD", 0.3m, 1500m), (item.Swap!.FromCode, item.Swap.FromUnits, item.Swap.ToCode, item.Swap.ToUnits, item.Swap.Value));
    }

    [Fact]
    public void Staking_income_comes_across_once_as_a_reward()
    {
        var income = Tx("""
            {"id":"6f1c","type":"cryptoIncome","date":"2025-06-01T00:00:00Z","value":12.5,"currency":"AUD","baseValue":12.5,"baseCurrency":"AUD",
             "incoming":[{"holding":{"id":2,"symbol":"ETH","exchange":"Crypto","holdingType":"Crypto"},"currency":"AUD","quantity":0.0025,"price":5000,"amount":12.5,"baseAmount":12.5}]}
            """);
        var trade = Tx("""
            {"id":"77","type":"cryptoTrade","date":"2025-06-01T00:00:00Z","tradeTypeId":15,"tradeType":"staking_reward","value":12.5,"currency":"AUD",
             "incoming":[{"holding":{"id":2,"symbol":"ETH","exchange":"Crypto","holdingType":"Crypto"},"currency":"AUD","quantity":0.0025,"price":5000,"amount":12.5}]}
            """);
        var plan = Plan(income, trade);
        var reward = plan.Items.Single(i => i.NavexaId == "6f1c");
        Assert.Equal(SyncStatus.Ready, reward.Status);
        Assert.Equal(SyncKind.Reward, reward.Kind);
        Assert.Equal(("ETH-AUD", 0.0025m, 12.5m, RewardKinds.Staking), (reward.Reward!.Code, reward.Reward.Units, reward.Reward.Value, reward.Reward.Kind));
        Assert.Equal(SyncStatus.Skipped, plan.Items.Single(i => i.NavexaId == "77").Status);

        // A staking trade with no income record is brought across itself
        Assert.Equal(SyncKind.Reward, Plan(trade).Items.Single().Kind);
    }

    [Fact]
    public void Crypto_withdrawals_are_left_out_and_deposits_with_a_cost_are_buys_without_cash()
    {
        const string coin = """{"holding":{"id":1,"symbol":"BTC","exchange":"Crypto","holdingType":"Crypto"},"currency":"AUD","quantity":0.1,"price":90000}""";
        var withdrawal = Plan(Tx($$"""{"id":"1","type":"cryptoTrade","date":"2025-01-01T00:00:00Z","tradeTypeId":101,"currency":"AUD","outgoing":[{{coin}}]}""")).Items.Single();
        Assert.Equal(SyncStatus.Skipped, withdrawal.Status);
        var deposit = NavexaSyncPlanner.Plan([Tx($$"""{"id":"2","type":"cryptoTrade","date":"2025-01-01T00:00:00Z","tradeTypeId":100,"currency":"AUD","incoming":[{{coin}}]}""")],
            new SyncSettings("AUD", "CommSec CDIA", null, new Dictionary<string, HoldingMapping?>(), new Dictionary<string, string?>(), new Dictionary<string, int?>()),
            new Dictionary<string, KnownItem>(), ExistingRecords.None).Items.Single();
        Assert.Equal(SyncStatus.Ready, deposit.Status);
        Assert.Equal("Buy", deposit.Trade!.Type);
        Assert.Null(deposit.Trade.BankAccount);
    }

    [Fact]
    public void Rewards_post_with_buys_before_swaps_and_sells()
    {
        var reward = new PlannedItem { NavexaType = "cryptoIncome", NavexaId = "1", Kind = SyncKind.Reward };
        var swap = new PlannedItem { NavexaType = "cryptoTrade", NavexaId = "2", Kind = SyncKind.Swap };
        Assert.True(reward.PostingOrder < swap.PostingOrder);
    }

    // ---------- Navexa CSV ----------

    [Fact]
    public void Navexa_files_map_the_crypto_exchange_and_bring_staking_rows_in_as_rewards()
    {
        const string csv = """
            Date,Symbol,Exchange,Type,Quantity,Price,Brokerage,Currency
            2025-04-07,BTC,Crypto,Buy,0.01243653,65166.09,0.18,AUD
            2025-06-01,ETH,Crypto,Staking Reward,0.0025,5000,0,AUD
            """;
        var result = NavexaParser.ParseTrades(csv, new NavexaOptions("AUD", Today: new DateTime(2026, 10, 27)));
        Assert.Empty(result.Errors);
        var buy = result.Rows[0];
        Assert.Equal(("CRYPTO", "BTC-AUD", "Buy"), (buy.Exchange, buy.Code, buy.Type));
        Assert.Empty(buy.Warnings);
        var reward = result.Rows[1];
        Assert.Equal(("CRYPTO", "ETH-AUD", "Reward"), (reward.Exchange, reward.Code, reward.Type));
        Assert.Equal(ImportRowStatus.Ready, reward.Status);

        var income = NavexaParser.ParseIncome("Payment Date,Symbol,Exchange,Gross Amount\n2025-06-01,ETH,Crypto,12.50\n", new NavexaOptions("AUD", Today: new DateTime(2026, 10, 27)));
        Assert.Equal(ImportRowStatus.Unsupported, income.Rows.Single().Status);
    }

    // ---------- Exchange files ----------

    private static CryptoPreview Read(string csv, string format = CryptoExchangeCsv.Auto) =>
        CryptoExchangeCsv.Parse(format, csv, new DateTime(2026, 10, 27));

    [Fact]
    public void Reads_a_coinbase_transaction_history()
    {
        const string csv = """
            You can use this transaction report to inform your likely tax obligations.
            Transactions
            User,someone@example.com,abc123
            ID,Timestamp,Transaction Type,Asset,Quantity Transacted,Price Currency,Price at Transaction,Subtotal,Total (inclusive of fees and/or spread),Fees and/or Spread,Notes
            1,2024-03-01 10:00:00 UTC,Buy,BTC,0.01,AUD,A$95000.00,A$950.00,A$965.00,A$15.00,Bought 0.01 BTC for A$965.00 AUD
            2,2024-04-01 10:00:00 UTC,Convert,BTC,0.005,AUD,A$100000.00,A$500.00,A$505.00,A$5.00,Converted 0.005 BTC to 0.12 ETH
            3,2024-05-01 10:00:00 UTC,Staking Income,ETH,0.001,AUD,A$4500.00,A$4.50,A$4.50,A$0.00,
            4,2024-06-01 10:00:00 UTC,Send,BTC,0.001,AUD,A$98000,,,,
            5,2024-06-02 10:00:00 UTC,Sell,ETH,0.05,AUD,"A$5,000.00",A$250.00,A$246.00,A$4.00,
            """;
        var p = Read(csv);
        Assert.Null(p.FileError);
        Assert.Equal("Coinbase", p.Source);
        Assert.Equal((4, 0, 1), (p.ValidCount, p.ErrorCount, p.SkippedCount));
        var rows = p.ValidRows.ToList();

        var buy = rows[0].Trade!;
        Assert.Equal((TradeType.Buy, "BTC-AUD", 0.01m, 95000m, 15m), (buy.Type, buy.Code, buy.Quantity, buy.Price, buy.Brokerage));
        Assert.Equal(new DateOnly(2024, 3, 1), buy.Date);

        var swap = rows[1].Swap!;
        Assert.Equal(("BTC-AUD", 0.005m, "ETH-AUD", 0.12m, 500m), (swap.FromCode, swap.FromUnits, swap.ToCode, swap.ToUnits, swap.Value));

        var reward = rows[2].Reward!;
        Assert.Equal(("ETH-AUD", 0.001m, 4.5m, RewardKinds.Staking), (reward.Code, reward.Units, reward.Value, reward.Kind));

        var sell = rows[3].Trade!;
        Assert.Equal((TradeType.Sell, "ETH-AUD", 5000m, 4m), (sell.Type, sell.Code, sell.Price, sell.Brokerage));
    }

    [Fact]
    public void Reads_a_coinspot_order_history_with_a_coin_for_coin_trade()
    {
        const string csv = """
            Transaction Date,Type,Market,Amount,Rate inc. fee,Rate ex. fee,Fee,Fee AUD (inc GST),GST AUD,Total AUD,Total (inc GST)
            01/03/2024 10:22 AM,Buy,BTC/AUD,0.01,95950,95000,9.5 AUD,9.50,0.86,959.50,959.50 AUD
            02/04/2024 11:00 AM,Sell,ETH/BTC,0.5,0.0505,0.05,0.0005 BTC,25.00,2.27,2500.00,0.0250 BTC
            """;
        var p = Read(csv);
        Assert.Equal("CoinSpot", p.Source);
        Assert.Equal(2, p.ValidCount);
        var buy = p.ValidRows.First().Trade!;
        Assert.Equal(("BTC-AUD", 0.01m, 95000m, 9.5m), (buy.Code, buy.Quantity, buy.Price, buy.Brokerage));
        var swap = p.ValidRows.Last().Swap!;
        Assert.Equal(("ETH-AUD", 0.5m, "BTC-AUD", 0.025m, 2500m), (swap.FromCode, swap.FromUnits, swap.ToCode, swap.ToUnits, swap.Value));
    }

    [Fact]
    public void Reads_independent_reserve_with_xbt_as_bitcoin()
    {
        const string csv = """
            Date Created,Date Completed,Order Guid,Order Type,Status,Volume Ordered,Volume Filled,Price,Average Price,Value,Currency Code (Primary),Currency Code (Secondary),Fee,Fee Currency Code
            2024-03-01 10:00:00,2024-03-01 10:00:05,a1b2,LimitBid,Filled,0.02,0.02,95000,95000,1900,Xbt,Aud,1.9,Aud
            2024-04-01 10:00:00,2024-04-01 10:00:05,c3d4,MarketOffer,Filled,0.01,0.01,,99000,990,Xbt,Aud,0.99,Aud
            """;
        var p = Read(csv);
        Assert.Equal("Independent Reserve", p.Source);
        Assert.Equal(2, p.ValidCount);
        var rows = p.ValidRows.Select(r => r.Trade!).ToList();
        Assert.Equal((TradeType.Buy, "BTC-AUD", 0.02m, 95000m, 1.9m), (rows[0].Type, rows[0].Code, rows[0].Quantity, rows[0].Price, rows[0].Brokerage));
        Assert.Equal((TradeType.Sell, 99000m, 0.99m), (rows[1].Type, rows[1].Price, rows[1].Brokerage));
    }

    [Fact]
    public void Reads_a_swyftx_style_file_and_leaves_out_deposits()
    {
        const string csv = """
            Date,Event,Asset,Amount,Rate (AUD),Total (AUD),Fee (AUD)
            2024-03-01,Buy,SOL,2,200,404,4
            2024-03-02,Deposit,AUD,1000,,,
            2024-03-05,Staking Reward,SOL,0.01,210,2.10,
            """;
        var p = Read(csv);
        Assert.Equal("Swyftx", p.Source);
        Assert.Equal((2, 1), (p.ValidCount, p.SkippedCount));
        var buy = p.ValidRows.First().Trade!;
        Assert.Equal(("SOL-AUD", 2m, 200m, 4m), (buy.Code, buy.Quantity, buy.Price, buy.Brokerage));
        Assert.Equal(2.10m, p.ValidRows.Last().Reward!.Value);
    }

    [Fact]
    public void A_coin_swap_with_no_aud_value_is_an_error()
    {
        const string csv = """
            Date,Type,Market,Amount,Price,Total,Fee
            2024-03-01,Buy,ETH/BTC,1,0.05,0.05,0
            """;
        var p = Read(csv, "headers");
        Assert.Equal(1, p.ErrorCount);
        Assert.Contains(p.Lines.Single().Errors, e => e.Contains("Record a crypto swap"));
    }

    [Fact]
    public void Flags_trades_already_in_the_portfolio()
    {
        const string csv = "Date,Type,Asset,Amount,Price,Fee\n2024-03-01,Buy,BTC,0.01,95000,0\n";
        var p = Read(csv);
        var key = ShareTradeRow.TradeKey("CRYPTO", "BTC-AUD", "Buy", new DateTime(2024, 3, 1), 0.01m, 95000m);
        var flagged = CryptoExchangeCsv.FlagDuplicates(p, new HashSet<string> { key });
        Assert.Contains(flagged.Lines.Single().Warnings, w => w.Contains("already in this portfolio"));
    }
}
