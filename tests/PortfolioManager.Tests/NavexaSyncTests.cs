using System.Net;
using System.Text;
using System.Text.Json;
using PortfolioManager.Core.Import;
using PortfolioManager.Core.NavexaSync;

namespace PortfolioManager.Tests;

public class NavexaSyncTests
{
    // Shapes copied from a real Navexa transactions response (ids and amounts changed)
    private const string Page = """
        {"page":1,"pageSize":250,"total":5,"items":[
          {"id":"101","type":"trade","date":"2024-03-01T00:00:00Z","tradeTypeId":1,"tradeType":"buy","value":1009.5,"currency":"AUD","baseValue":1009.5,"baseCurrency":"AUD",
           "incoming":[{"id":"101","holding":{"id":555,"symbol":"CBA","exchange":"AXW","displayExchange":"ASX","holdingTypeId":1,"holdingType":"STOCK"},"currency":"AUD","quantity":10,"price":100,"amount":1009.5,"baseAmount":1009.5,"tradeTypeId":1,"tradeType":"buy","brokerage":9.5}],
           "outgoing":[{"currency":"AUD","quantity":1009.5,"price":1,"amount":1009.5,"baseAmount":1009.5}],
           "notes":"First buy","isConfirmed":false,"autoAdded":false,"lastModifiedDate":"2024-03-02T01:00:00Z"},
          {"id":"4794305","type":"dividend","date":"2024-09-14T00:00:00Z","value":70,"currency":"AUD","baseValue":70,"baseCurrency":"AUD",
           "incoming":[{"id":"4794305","holding":{"id":555,"symbol":"CBA","exchange":"AXW","holdingTypeId":1,"holdingType":"STOCK"},"currency":"AUD","quantity":1,"price":70,"amount":70,"baseAmount":70}],
           "dividend":{"grossAmount":70,"netAmount":70,"franked":70,"unfranked":0,"frankingCredits":30,"isForeignIncome":false,"isReinvested":false,"exDividendDate":"2024-08-20T00:00:00Z","paymentDate":"2024-09-14T00:00:00Z"},
           "notes":"Final dividend","isConfirmed":true,"autoAdded":false,"lastModifiedDate":"2024-09-14T20:11:43.8389431Z"},
          {"id":"14458368","type":"cashTransaction","date":"2024-09-14T00:00:00Z","tradeTypeId":107,"tradeType":"income_payout","value":70,"currency":"AUD","baseValue":70,"baseCurrency":"AUD",
           "incoming":[{"id":"14458368","holding":{"id":961773,"symbol":"CommSec cash","exchange":"Cash","holdingTypeId":3,"holdingType":"CASH"},"currency":"AUD","quantity":70,"price":1,"amount":70,"baseAmount":70}],
           "linkedTo":{"type":"dividend","id":"4794305","holdingId":555},"isConfirmed":true,"autoAdded":true},
          {"id":"14458370","type":"cashTransaction","date":"2024-10-01T00:00:00Z","tradeTypeId":100,"tradeType":"deposit","value":500,"currency":"AUD","baseValue":500,"baseCurrency":"AUD",
           "incoming":[{"id":"14458370","holding":{"id":961773,"symbol":"CommSec cash","exchange":"Cash","holdingTypeId":3,"holdingType":"CASH"},"currency":"AUD","quantity":500,"price":1,"amount":500,"baseAmount":500}],
           "notes":"Top up","isConfirmed":true,"autoAdded":false},
          {"id":14427969,"type":"trade","date":"2026-10-03T00:00:00Z","tradeTypeId":2,"tradeType":"sell","value":1520.36,"currency":"AUD","baseValue":1520.36,"baseCurrency":"AUD",
           "incoming":[{"currency":"AUD","quantity":1520.36,"price":1,"amount":1520.36,"baseAmount":1520.36}],
           "outgoing":[{"id":"14427969","holding":{"id":961769,"symbol":"Brickx Managed Fund: WRF01 - 20 Blackall Street WOOMBYE QLD","exchange":"CUSTOM","displayExchange":"Custom","holdingTypeId":4,"holdingType":"CUSTOM"},"currency":"AUD","quantity":8,"price":191,"amount":1520.36,"baseAmount":1520.36,"tradeTypeId":2,"tradeType":"sell","brokerage":7.64}],
           "isConfirmed":false,"autoAdded":false}
        ]}
        """;

    private static List<NavexaApiTransaction> Items() =>
        JsonSerializer.Deserialize<JsonElement>(Page).GetProperty("items").Deserialize<List<NavexaApiTransaction>>(NavexaApiClient.Json)!;

    private static SyncSettings Settings(
        string? tradeBank = null,
        DateTime? from = null,
        Dictionary<string, HoldingMapping?>? holdings = null,
        Dictionary<string, string?>? cash = null,
        Dictionary<string, int?>? types = null) =>
        new("AUD", tradeBank, from, holdings ?? [], cash ?? [], types ?? []);

    private static SyncPlan Plan(IEnumerable<NavexaApiTransaction> items, SyncSettings settings,
        Dictionary<string, KnownItem>? known = null, ExistingRecords? existing = null) =>
        NavexaSyncPlanner.Plan(items, settings, known ?? [], existing ?? ExistingRecords.None);

    private static PlannedItem Find(SyncPlan plan, string id) => plan.Items.Single(i => i.NavexaId == id);

    [Fact]
    public void Reads_navexa_transactions_including_numeric_ids()
    {
        var items = Items();
        Assert.Equal(5, items.Count);
        Assert.Equal("14427969", items[4].Id);
        Assert.Equal("555", items[0].Incoming![0].Holding!.Id);
        Assert.Equal(30m, items[1].Dividend!.FrankingCredits);
        Assert.Equal("dividend", items[2].LinkedTo!.Type);
    }

    [Fact]
    public void Asx_buy_is_ready_with_navexa_exchange_mapped()
    {
        var plan = Plan(Items(), Settings());
        var buy = Find(plan, "101");
        Assert.Equal(SyncStatus.Ready, buy.Status);
        Assert.Equal(new SyncTrade("ASX", "CBA", "CBA", "Buy", new DateTime(2024, 3, 1), 10, 100m, 9.5m, "AUD", 1m, "From Navexa: First buy", null), buy.Trade);
    }

    [Fact]
    public void Trades_settle_in_the_chosen_bank_account()
    {
        var plan = Plan(Items(), Settings(tradeBank: "CommSec CDIA"));
        Assert.Equal("CommSec CDIA", Find(plan, "101").Trade!.BankAccount);
    }

    [Fact]
    public void Dividend_with_franking_is_ready_and_its_linked_cash_is_skipped()
    {
        var plan = Plan(Items(), Settings());
        var div = Find(plan, "4794305");
        Assert.Equal(SyncStatus.Ready, div.Status);
        Assert.Equal(new SyncPayout("ASX", "CBA", new DateTime(2024, 9, 14), 70m, 0m, 30m, 0m, 70m, "From Navexa: Final dividend"), div.Payout);
        var linked = Find(plan, "14458368");
        Assert.Equal(SyncStatus.Skipped, linked.Status);
        Assert.Contains("cash side", linked.Message);
    }

    [Fact]
    public void Custom_holding_asks_for_a_code_with_a_suggestion_then_is_ready()
    {
        var plan = Plan(Items(), Settings());
        var sell = Find(plan, "14427969");
        Assert.Equal(SyncStatus.NeedsInput, sell.Status);
        Assert.Equal(InputNeed.Mapping, sell.Need);
        var need = Assert.Single(plan.Setup.Holdings);
        Assert.Equal(("961769", "CUSTOM", "WRF01"), (need.Key, need.SuggestedExchange, need.SuggestedCode));

        plan = Plan(Items(), Settings(holdings: new() { ["961769"] = new HoldingMapping("CUSTOM", "WRF01") }));
        sell = Find(plan, "14427969");
        Assert.Equal(SyncStatus.Ready, sell.Status);
        Assert.Equal(("Sell", 8, 191m, 7.64m), (sell.Trade!.Type, sell.Trade.Quantity, sell.Trade.Price, sell.Trade.Brokerage));
        Assert.Empty(plan.Setup.Holdings);
    }

    [Fact]
    public void Holding_mapped_to_skip_is_not_brought_across()
    {
        var plan = Plan(Items(), Settings(holdings: new() { ["961769"] = null }));
        Assert.Equal(SyncStatus.Skipped, Find(plan, "14427969").Status);
    }

    [Fact]
    public void Cash_account_must_be_mapped_before_its_deposit_comes_across()
    {
        var plan = Plan(Items(), Settings());
        Assert.Equal(SyncStatus.NeedsInput, Find(plan, "14458370").Status);
        Assert.Equal("961773", Assert.Single(plan.Setup.CashAccounts).Key);

        plan = Plan(Items(), Settings(cash: new() { ["961773"] = "CommSec CDIA" }));
        var deposit = Find(plan, "14458370");
        Assert.Equal(SyncStatus.Ready, deposit.Status);
        // A deposit needs no ledger account choice: it goes against Owner's Capital as one entered by hand
        Assert.Equal(new SyncCash("CommSec CDIA", new DateTime(2024, 10, 1), 500m, 0m, "AUD", "From Navexa: Top up", null), deposit.Cash);

        plan = Plan(Items(), Settings(cash: new() { ["961773"] = null }));
        Assert.Equal(SyncStatus.Skipped, Find(plan, "14458370").Status);
    }

    [Fact]
    public void Fee_asks_for_its_ledger_account_then_posts_against_it()
    {
        var fee = Tx("""
            {"id":"9","type":"cashTransaction","date":"2025-01-31T00:00:00Z","tradeTypeId":104,"tradeType":"fee","value":5,"currency":"AUD",
            "outgoing":[{"holding":{"id":961773,"symbol":"CommSec cash","exchange":"Cash","holdingType":"CASH"},"currency":"AUD","amount":5}],"isConfirmed":true}
            """);
        var cash = new Dictionary<string, string?> { ["961773"] = "CommSec CDIA" };

        var plan = Plan([fee], Settings(cash: cash));
        Assert.Equal(SyncStatus.NeedsInput, plan.Items[0].Status);
        Assert.Equal("fee", Assert.Single(plan.Setup.CashTypes).Key);

        plan = Plan([fee], Settings(cash: cash, types: new() { ["fee"] = 42 }));
        Assert.Equal(new SyncCash("CommSec CDIA", new DateTime(2025, 1, 31), 0m, 5m, "AUD", "From Navexa: Fee", 42), plan.Items[0].Cash);
    }

    [Fact]
    public void Franked_dividend_without_a_franking_credit_asks_for_one_with_a_30_percent_suggestion()
    {
        var div = Tx("""
            {"id":"7","type":"dividend","date":"2025-03-20T00:00:00Z","value":140,"currency":"AUD","baseValue":140,
            "incoming":[{"holding":{"id":555,"symbol":"CBA","exchange":"AXW","holdingType":"STOCK"}}],
            "dividend":{"grossAmount":140,"netAmount":140,"franked":140,"unfranked":0,"frankingCredits":0,"paymentDate":"2025-03-20T00:00:00Z"},"isConfirmed":true}
            """);

        var plan = Plan([div], Settings());
        var item = plan.Items[0];
        Assert.Equal((SyncStatus.NeedsInput, InputNeed.FrankingCredit), (item.Status, item.Need));
        Assert.Equal(60m, item.Suggested.FrankingCredit);

        var known = new Dictionary<string, KnownItem> { ["dividend:7"] = new(SyncStatus.NeedsInput, null, new SyncInput { FrankingCredit = 46.67m }) };
        item = Plan([div], Settings(), known).Items[0];
        Assert.Equal(SyncStatus.Ready, item.Status);
        Assert.Equal(46.67m, item.Payout!.FrankingCredit);
    }

    [Fact]
    public void Unconfirmed_dividend_navexa_added_itself_waits()
    {
        var div = Tx("""
            {"id":"8","type":"dividend","date":"2025-03-20T00:00:00Z","value":10,"currency":"AUD",
            "incoming":[{"holding":{"id":555,"symbol":"CBA","exchange":"AXW","holdingType":"STOCK"}}],
            "dividend":{"netAmount":10,"unfranked":10},"isConfirmed":false,"autoAdded":true}
            """);
        Assert.Equal(SyncStatus.Waiting, Plan([div], Settings()).Items[0].Status);
    }

    [Fact]
    public void Reinvested_dividend_pairs_with_navexas_drp_trade()
    {
        var div = Tx("""
            {"id":"20","type":"dividend","date":"2025-04-01T00:00:00Z","value":105,"currency":"AUD",
            "incoming":[{"holding":{"id":555,"symbol":"CBA","exchange":"AXW","holdingType":"STOCK"}}],
            "dividend":{"netAmount":105,"franked":105,"unfranked":0,"frankingCredits":45,"isReinvested":true,"paymentDate":"2025-04-01T00:00:00Z"},"isConfirmed":true}
            """);
        var units = Tx("""
            {"id":"21","type":"trade","date":"2025-04-01T00:00:00Z","tradeTypeId":8,"tradeType":"dividend_reinvestment","value":100,"currency":"AUD",
            "incoming":[{"holding":{"id":555,"symbol":"CBA","exchange":"AXW","holdingType":"STOCK"},"quantity":1,"price":100}],"isConfirmed":true}
            """);

        var plan = Plan([div, units], Settings());
        var drp = Find(plan, "20");
        Assert.Equal(SyncStatus.Ready, drp.Status);
        Assert.Equal(new SyncDrp("ASX", "CBA", new DateTime(2025, 4, 1), 105m, 0m, 45m, 1, 100m, "From Navexa"), drp.Drp);
        Assert.Equal(SyncStatus.Skipped, Find(plan, "21").Status);
        Assert.Contains("with its dividend", Find(plan, "21").Message);

        // Without Navexa's units the user is asked for them
        var alone = Plan([div], Settings()).Items[0];
        Assert.Equal((SyncStatus.NeedsInput, InputNeed.Drp), (alone.Status, alone.Need));
    }

    [Fact]
    public void Split_asks_for_its_ratio()
    {
        var split = Tx("""
            {"id":"30","type":"trade","date":"2025-05-01T00:00:00Z","tradeTypeId":3,"tradeType":"split",
            "incoming":[{"holding":{"id":555,"symbol":"CBA","exchange":"AXW","holdingType":"STOCK"},"quantity":10}],"isConfirmed":true}
            """);
        Assert.Equal(InputNeed.SplitRatio, Plan([split], Settings()).Items[0].Need);

        var known = new Dictionary<string, KnownItem> { ["trade:30"] = new(SyncStatus.NeedsInput, null, new SyncInput { SplitNewUnits = 2, SplitOldUnits = 1 }) };
        var item = Plan([split], Settings(), known).Items[0];
        Assert.Equal(new SyncSplit("ASX", "CBA", new DateTime(2025, 5, 1), 2, 1), item.Split);

        // 1 for 2 isn't a split
        known["trade:30"] = new(SyncStatus.NeedsInput, null, new SyncInput { SplitNewUnits = 1, SplitOldUnits = 2 });
        Assert.Equal(SyncStatus.NeedsInput, Plan([split], Settings(), known).Items[0].Status);
    }

    [Fact]
    public void Foreign_trade_rate_is_stored_as_foreign_per_portfolio_unit()
    {
        var buy = Tx("""
            {"id":"40","type":"trade","date":"2025-02-03T00:00:00Z","tradeTypeId":1,"value":1000,"currency":"USD","baseValue":1540,"exchangeRate":1.54,
            "incoming":[{"holding":{"id":777,"symbol":"AAPL","exchange":"NAS","holdingType":"STOCK"},"currency":"USD","quantity":5,"price":200}],"isConfirmed":true}
            """);
        var trade = Plan([buy], Settings()).Items[0].Trade!;
        Assert.Equal(("NASDAQ", "AAPL", "USD"), (trade.Exchange, trade.Code, trade.Currency));
        Assert.Equal(Math.Round(1m / 1.54m, 6), trade.ExchangeRate);
    }

    [Fact]
    public void Records_added_earlier_are_never_planned_again_and_edits_are_flagged()
    {
        var known = new Dictionary<string, KnownItem>
        {
            ["trade:101"] = new(SyncStatus.Imported, new DateTime(2024, 3, 2, 1, 0, 0, DateTimeKind.Utc), null),
            ["dividend:4794305"] = new(SyncStatus.Imported, new DateTime(2024, 9, 1, 0, 0, 0, DateTimeKind.Utc), null),
        };
        var plan = Plan(Items(), Settings(), known);
        Assert.Equal(SyncStatus.Imported, Find(plan, "101").Status);
        Assert.False(Find(plan, "101").ChangedInNavexa);
        Assert.Equal(SyncStatus.Imported, Find(plan, "4794305").Status);
        Assert.True(Find(plan, "4794305").ChangedInNavexa);
        Assert.DoesNotContain(plan.Ready, i => i.NavexaId is "101" or "4794305");
    }

    [Fact]
    public void Trade_already_entered_by_hand_asks_before_adding()
    {
        var existing = new ExistingRecords([new ExistingTrade("ASX", "CBA", "Buy", new DateTime(2024, 3, 1), 10, 100m)], [], []);
        var buy = Find(Plan(Items(), Settings(), existing: existing), "101");
        Assert.Equal(InputNeed.ConfirmNotDuplicate, buy.Need);

        var same = new Dictionary<string, KnownItem> { ["trade:101"] = new(SyncStatus.NeedsInput, null, new SyncInput { AlreadyHere = true }) };
        Assert.Equal(SyncStatus.Imported, Find(Plan(Items(), Settings(), same, existing), "101").Status);

        var different = new Dictionary<string, KnownItem> { ["trade:101"] = new(SyncStatus.NeedsInput, null, new SyncInput { AddAnyway = true }) };
        Assert.Equal(SyncStatus.Ready, Find(Plan(Items(), Settings(), different, existing), "101").Status);
    }

    [Fact]
    public void Records_before_the_start_date_are_skipped()
    {
        var plan = Plan(Items(), Settings(from: new DateTime(2024, 6, 1)));
        Assert.Equal(SyncStatus.Skipped, Find(plan, "101").Status);
        Assert.Equal(SyncStatus.Ready, Find(plan, "4794305").Status);
    }

    [Fact]
    public void Fractional_units_and_crypto_are_not_brought_across()
    {
        var fractional = Tx("""
            {"id":"50","type":"trade","date":"2025-02-03T00:00:00Z","tradeTypeId":1,"currency":"AUD",
            "incoming":[{"holding":{"id":555,"symbol":"CBA","exchange":"AXW","holdingType":"STOCK"},"quantity":1.5,"price":100}]}
            """);
        var crypto = Tx("""{"id":"51","type":"cryptoTrade","date":"2025-02-03T00:00:00Z"}""");
        var plan = Plan([fractional, crypto], Settings());
        Assert.All(plan.Items, i => Assert.Equal(SyncStatus.Skipped, i.Status));
    }

    [Fact]
    public void Buys_post_before_sells_on_the_same_day()
    {
        var buy = new PlannedItem { NavexaType = "trade", NavexaId = "2", Kind = SyncKind.Trade, Trade = Trade("Buy") };
        var sell = new PlannedItem { NavexaType = "trade", NavexaId = "1", Kind = SyncKind.Trade, Trade = Trade("Sell") };
        Assert.True(buy.PostingOrder < sell.PostingOrder);

        static SyncTrade Trade(string type) => new("ASX", "CBA", "", type, DateTime.Today, 1, 1, 0, "AUD", 1, "", null);
    }

    [Theory]
    [InlineData("Brickx Managed Fund: ARN01 - 213 Princes Hwy Arncliffe NSW 2205", "ARN01")]
    [InlineData("My Unlisted Fund", "MY")]
    [InlineData("Property: Unit 4", "UNIT")]
    public void Suggests_a_short_code(string symbol, string expected) =>
        Assert.Equal(expected, NavexaSyncPlanner.SuggestCode(symbol));

    [Fact]
    public void Saved_mappings_become_planner_settings()
    {
        var connection = new NavexaConnection(1, "x", "1234", 9, "Mine", "Bank", null, SyncSchedule.Daily, null, null);
        var settings = NavexaSyncService.Settings("AUD", connection, [
            new(NavexaMapping.Holding, "1", "Fund", false, "custom", "wrf01", null),
            new(NavexaMapping.Holding, "2", "Other", true, null, null, null),
            new(NavexaMapping.Holding, "3", "Half done", false, "", "", null),
            new(NavexaMapping.CashAccount, "10", "Cash", false, null, " CDIA ", null),
            new(NavexaMapping.CashType, "fee", "Fee", false, null, null, 42),
            new(NavexaMapping.CashType, "interest_charged", "Interest", false, null, null, null),
        ]);
        Assert.Equal(new HoldingMapping("CUSTOM", "WRF01"), settings.Holdings["1"]);
        Assert.Null(settings.Holdings["2"]);
        Assert.False(settings.Holdings.ContainsKey("3"));
        Assert.Equal("CDIA", settings.CashAccounts["10"]);
        Assert.Equal(42, settings.CashTypes["fee"]);
        Assert.True(settings.CashTypes.ContainsKey("interest_charged"));
        Assert.Equal("Bank", settings.TradeBankAccount);
    }

    [Fact]
    public void Proc_parameters_match_the_procs()
    {
        var t = new SyncTrade("ASX", "CBA", "Commonwealth Bank", "Buy", new DateTime(2024, 3, 1), 10, 100m, 9.5m, "AUD", 1m, "c", null);
        var plain = NavexaSyncParameters.Trade(5, t);
        Assert.Equal(13, plain.Count);
        Assert.Equal("c", plain["strComment"]);
        Assert.True(plain.ContainsKey("iBankStatementId"));

        var withCash = NavexaSyncParameters.Trade(5, t with { BankAccount = "CDIA" });
        Assert.Equal("CDIA", withCash["strBankAccountName"]);
        Assert.Equal("c", withCash["strShareTradeComment"]);
        Assert.False(withCash.ContainsKey("strComment"));
        Assert.Equal(15, withCash.Count);

        var drp = NavexaSyncParameters.Drp(5, new SyncDrp("ASX", "CBA", DateTime.Today, 100m, 5m, 42.86m, 1, 104m, "x"));
        Assert.Equal(105d, drp["dblTotalPayout"]);

        var split = NavexaSyncParameters.Split(5, new SyncSplit("ASX", "CBA", DateTime.Today, 1, 10));
        Assert.Equal(0.1d, (double)split["dblSplitRatio"]!, 10);

        var cash = NavexaSyncParameters.Cash(5, new SyncCash("CDIA", DateTime.Today, 0, 5, "AUD", "Fee", 42));
        Assert.Equal(42, cash["iContraAccountID"]);
        Assert.Equal(5m, cash["dblWithdrawalAmount"]);
    }

    // ---------- API client ----------

    [Fact]
    public async Task Client_sends_the_key_and_reads_every_page()
    {
        var requests = new List<HttpRequestMessage>();
        var pages = new Queue<string>([
            """{"page":1,"pageSize":250,"total":2,"items":[{"id":"1","type":"trade","date":"2024-01-01T00:00:00Z"}]}""",
            """{"page":2,"pageSize":250,"total":2,"items":[{"id":"2","type":"trade","date":"2024-01-02T00:00:00Z"}]}""",
        ]);
        var client = Client(req =>
        {
            requests.Add(req);
            return Json(HttpStatusCode.OK, pages.Dequeue());
        });

        var items = await client.GetTransactionsAsync("secret-key", 31646, new DateOnly(2026, 10, 15));

        Assert.Equal(["1", "2"], items.Select(i => i.Id));
        Assert.Equal(2, requests.Count);
        Assert.All(requests, r => Assert.Equal("secret-key", r.Headers.GetValues("x-api-key").Single()));
        Assert.Equal("https://api.navexa.com.au/v1/portfolios/31646/transactions", requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task Client_falls_back_to_the_authorization_header_on_401()
    {
        var seen = new List<string>();
        var client = Client(req =>
        {
            if (req.Headers.Contains("x-api-key"))
            {
                seen.Add("x-api-key");
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }
            seen.Add(req.Headers.GetValues("Authorization").Single());
            return Json(HttpStatusCode.OK, """[{"id":3092,"name":"Craig","baseCurrencyCode":"AUD"},{"id":2568,"name":"Group","isPortfolioGroup":true}]""");
        });

        var portfolios = await client.GetPortfoliosAsync("k1");
        Assert.Equal(["x-api-key", "k1"], seen);
        Assert.Equal(3092, Assert.Single(portfolios).Id);
    }

    [Fact]
    public async Task Client_reports_a_refused_key_plainly()
    {
        var client = Client(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var ex = await Assert.ThrowsAsync<NavexaApiException>(() => client.GetPortfoliosAsync("bad"));
        Assert.Contains("didn't accept the API key", ex.Message);
    }

    [Fact]
    public async Task Client_tries_the_api_path_when_navexa_answers_404()
    {
        var seen = new List<string>();
        var client = Client(req =>
        {
            seen.Add(req.RequestUri!.ToString());
            return req.RequestUri!.AbsolutePath.StartsWith("/api/")
                ? Json(HttpStatusCode.OK, """{"page":1,"pageSize":250,"total":1,"items":[{"id":"1","type":"trade","date":"2024-01-01T00:00:00Z"}]}""")
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var items = await client.GetTransactionsAsync("k", 53598, new DateOnly(2026, 10, 19));

        Assert.Single(items);
        Assert.Equal(["https://api.navexa.com.au/v1/portfolios/53598/transactions",
            "https://api.navexa.com.au/api/v1/portfolios/53598/transactions"], seen);
    }

    [Fact]
    public async Task Client_names_the_portfolio_when_neither_path_finds_it()
    {
        var client = Client(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var ex = await Assert.ThrowsAsync<NavexaApiException>(() => client.GetTransactionsAsync("k", 53598, new DateOnly(2026, 10, 19)));
        Assert.Contains("portfolio 53598", ex.Message);
    }

    [Fact]
    public void Portfolios_are_read_from_a_list_or_the_grouped_shape()
    {
        var grouped = JsonSerializer.Deserialize<JsonElement>("""
            {"portfolios":[{"id":31646,"name":"Tony"}],"sharedPortfolios":[{"id":7,"name":"Alice"}],
             "portfolioGroups":[{"id":2568,"name":"Group","isPortfolioGroup":true}]}
            """);
        Assert.Equal([7, 31646], NavexaApiClient.ReadPortfolios(grouped).Select(p => p.Id));
        Assert.Equal(new Uri("https://api.navexa.com.au/api/"), NavexaApiClient.AlternateBase(new Uri("https://api.navexa.com.au/")));
        Assert.Equal(new Uri("https://api.navexa.com.au/"), NavexaApiClient.AlternateBase(new Uri("https://api.navexa.com.au/api/")));
    }

    [Fact]
    public async Task A_typed_navexa_portfolio_id_takes_its_name_from_the_list_or_a_plain_one()
    {
        var listed = new NavexaSyncService(null!, Client(_ => Json(HttpStatusCode.OK, """[{"id":53598,"name":"Darcy Asset Management"}]""")), new NoProtector());
        Assert.Equal("Darcy Asset Management", await listed.NavexaPortfolioNameAsync("k", 53598));
        Assert.Equal("Navexa portfolio 777", await listed.NavexaPortfolioNameAsync("k", 777));

        var noList = new NavexaSyncService(null!, Client(_ => new HttpResponseMessage(HttpStatusCode.NotFound)), new NoProtector());
        Assert.Equal("Navexa portfolio 53598", await noList.NavexaPortfolioNameAsync("k", 53598));

        var refused = new NavexaSyncService(null!, Client(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)), new NoProtector());
        await Assert.ThrowsAsync<NavexaApiException>(() => refused.NavexaPortfolioNameAsync("bad", 53598));
    }

    private sealed class NoProtector : INavexaKeyProtector
    {
        public string Protect(string apiKey) => apiKey;
        public string? Unprotect(string protectedKey) => protectedKey;
    }

    [Fact]
    public void Retry_after_is_capped()
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(3600));
        Assert.Equal(TimeSpan.FromSeconds(60), NavexaApiClient.RetryDelay(response, 0));
    }

    private static NavexaApiTransaction Tx(string json) => JsonSerializer.Deserialize<NavexaApiTransaction>(json, NavexaApiClient.Json)!;

    private static NavexaApiClient Client(Func<HttpRequestMessage, HttpResponseMessage> handler) =>
        new(new HttpClient(new StubHandler(handler)) { BaseAddress = NavexaApiClient.DefaultBaseAddress });

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }
}
