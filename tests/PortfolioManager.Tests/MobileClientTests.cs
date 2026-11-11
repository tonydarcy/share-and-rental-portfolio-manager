using System.Net;
using System.Text;
using System.Text.Json;
using PortfolioManager.Mobile.Client;
using PortfolioManager.Web.Api;
using Site = PortfolioManager.Core.Holdings;

namespace PortfolioManager.Tests;

public class MobileClientTests
{
    [Theory]
    [InlineData("app.shareportfoliomanager.com", "https://app.shareportfoliomanager.com")]
    [InlineData(" https://app.shareportfoliomanager.com/ ", "https://app.shareportfoliomanager.com")]
    [InlineData("https://example.com/pm", "https://example.com/pm")]
    [InlineData("http://localhost:5000", "http://localhost:5000")]
    [InlineData("http://10.0.2.2:5000", "http://10.0.2.2:5000")]
    [InlineData("http://example.com", null)]
    [InlineData("ftp://example.com", null)]
    [InlineData("https://example.com/?x=1", null)]
    [InlineData("", null)]
    public void Site_address_is_cleaned_and_https_only(string typed, string? expected) =>
        Assert.Equal(expected, ServerAddress.Normalise(typed));

    [Fact]
    public void Pkce_challenge_matches_the_RFC_example()
    {
        // RFC 7636 appendix B
        var pkce = Pkce.FromVerifier("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk");
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", pkce.Challenge);
        var made = Pkce.Create();
        Assert.InRange(made.Verifier.Length, 43, 128);
        Assert.Equal(Pkce.FromVerifier(made.Verifier).Challenge, made.Challenge);
        Assert.NotEqual(Pkce.NewState(), Pkce.NewState());
    }

    [Fact]
    public void The_site_accepts_the_apps_return_address()
    {
        Assert.Equal(OAuthServer.AppScheme, SignInClient.RedirectScheme);
        Assert.True(OAuthServer.AllowedRedirect(SignInClient.RedirectUri));
    }

    [Fact]
    public void Sign_in_page_address_carries_pkce_and_state()
    {
        var pkce = Pkce.FromVerifier("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk");
        var url = SignInClient.AuthorizeUrl("https://example.com", "pm_abc+/=", pkce, "s1").ToString();
        Assert.StartsWith("https://example.com/Account/Connect?response_type=code&client_id=pm_abc%2B%2F%3D&", url);
        Assert.Contains("redirect_uri=com.shareportfoliomanager.app%3A%2Foauth", url);
        Assert.Contains("code_challenge=E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM&code_challenge_method=S256&state=s1", url);
    }

    [Fact]
    public void Callback_needs_the_same_state()
    {
        Assert.Equal(new SignInCallback("c1", null), SignInClient.ReadCallback(new Dictionary<string, string> { ["code"] = "c1", ["state"] = "s" }, "s"));
        Assert.Null(SignInClient.ReadCallback(new Dictionary<string, string> { ["code"] = "c1", ["state"] = "other" }, "s").Code);
        Assert.Equal("Sign-in was cancelled.",
            SignInClient.ReadCallback(new Dictionary<string, string> { ["error"] = "access_denied", ["state"] = "s" }, "s").Error);
        Assert.Equal("Bad", SignInClient.ReadCallback(
            new Dictionary<string, string> { ["error"] = "invalid_request", ["error_description"] = "Bad", ["state"] = "s" }, "s").Error);
        Assert.NotNull(SignInClient.ReadCallback(new Dictionary<string, string> { ["state"] = "s" }, "s").Error);
    }

    [Fact]
    public async Task Registers_then_swaps_the_code_for_a_key()
    {
        var site = new FakeSite(request => request.RequestUri!.AbsolutePath switch
        {
            "/oauth/register" => (HttpStatusCode.Created, """{"client_id":"pm_1"}"""),
            "/oauth/token" => (HttpStatusCode.OK, """{"access_token":"key1","token_type":"Bearer"}"""),
            _ => (HttpStatusCode.NotFound, "{}"),
        });
        var client = new SignInClient(new HttpClient(site));
        var pkce = Pkce.Create();

        Assert.Equal("pm_1", await client.RegisterAsync("https://example.com"));
        Assert.Contains(SignInClient.RedirectUri, site.Bodies[0]);
        Assert.Equal("key1", await client.SwapCodeAsync("https://example.com", "pm_1", "c1", pkce));
        Assert.Contains("grant_type=authorization_code", site.Bodies[1]);
        Assert.Contains("code_verifier=" + pkce.Verifier, site.Bodies[1]);
    }

    [Fact]
    public async Task Refused_code_shows_the_sites_words()
    {
        var site = new FakeSite(_ => (HttpStatusCode.BadRequest, """{"error":"invalid_grant","error_description":"That sign-in code isn't right."}"""));
        var e = await Assert.ThrowsAsync<SiteException>(() =>
            new SignInClient(new HttpClient(site)).SwapCodeAsync("https://example.com", "pm_1", "c1", Pkce.Create()));
        Assert.Equal("That sign-in code isn't right.", e.Message);

        var html = new FakeSite(_ => (HttpStatusCode.OK, "<html></html>"));
        await Assert.ThrowsAsync<SiteException>(() => new SignInClient(new HttpClient(html)).RegisterAsync("https://example.com"));
    }

    [Fact]
    public async Task Api_sends_the_key_and_reads_holdings_as_the_site_writes_them()
    {
        var summary = new Site.HoldingsSummary(
            [new Site.Holding("BHP", "ASX", 100.5m, 45.1m, 4532.55m, 532.55m, 120m, 652.55m, "Up", 4000m)],
            new Site.HoldingsTotal(4532.55m, 532.55m, 120m, 652.55m, 4000m));
        var site = new FakeSite(_ => (HttpStatusCode.OK, JsonSerializer.Serialize(summary, AgentJson.Options)));
        var api = new SiteApi(new HttpClient(site), "https://example.com", "key1");

        var holdings = await api.GetHoldingsAsync(7);

        Assert.Equal("https://example.com/api/v1/portfolios/7/holdings", site.Requests[0].RequestUri!.ToString());
        Assert.Equal("Bearer key1", site.Requests[0].Headers.Authorization!.ToString());
        var bhp = Assert.Single(holdings.Holdings);
        Assert.Equal(("BHP", 100.5m, 4532.55m, 532.55m, 4000m), (bhp.StockCode, bhp.Units, bhp.MarketValue, bhp.CapitalGain, bhp.Cost));
        Assert.Equal(4532.55m, holdings.Total.MarketValue);
    }

    [Fact]
    public async Task Api_reads_portfolios()
    {
        var site = new FakeSite(_ => (HttpStatusCode.OK,
            """[{"portfolioId":3,"name":"Sample","entityType":"Individual","role":"Owner","canEdit":true}]"""));
        var list = await new SiteApi(new HttpClient(site), "https://example.com", "k").ListPortfoliosAsync();
        Assert.Equal(new PortfolioSummary(3, "Sample", "Individual", "Owner", true), Assert.Single(list));
    }

    [Fact]
    public async Task Revoked_key_signs_the_app_out_and_other_errors_use_the_sites_words()
    {
        var revoked = new FakeSite(_ => (HttpStatusCode.Unauthorized, ""));
        var e = await Assert.ThrowsAsync<SiteException>(() => new SiteApi(new HttpClient(revoked), "https://example.com", "k").ListPortfoliosAsync());
        Assert.True(e.SignedOut);

        var refused = new FakeSite(_ => (HttpStatusCode.Forbidden, """{"error":"You can't see that portfolio."}"""));
        e = await Assert.ThrowsAsync<SiteException>(() => new SiteApi(new HttpClient(refused), "https://example.com", "k").GetHoldingsAsync(1));
        Assert.False(e.SignedOut);
        Assert.Equal("You can't see that portfolio.", e.Message);
    }

    [Fact]
    public async Task Slow_site_gives_a_plain_message()
    {
        var slow = new FakeSite(_ => throw new TaskCanceledException("timeout"));
        var e = await Assert.ThrowsAsync<SiteException>(() => new SiteApi(new HttpClient(slow), "https://example.com", "k").ListPortfoliosAsync());
        Assert.Contains("too long", e.Message);
    }

    [Fact]
    public async Task Api_reads_the_dashboard_as_the_site_writes_it()
    {
        // The same shape as AgentTools.Dashboard
        var written = new
        {
            Currency = "AUD", TotalPortfolioValue = 15000m, TotalShareValue = 12000m, TotalCashValue = 3000m,
            LatestMove = new PortfolioManager.Core.Dashboard.DayMove(new DateOnly(2026, 11, 10), -120.5m, -0.01),
            Holdings = new Site.HoldingsTotal(12000m, 2000m, 300m, 2300m, 10000m),
            TopHoldings = new[] { new { ExchangeCode = "ASX", StockCode = "CBA", Units = 50m, Price = 160m, MarketValue = 8000m,
                CapitalGain = 1500m, GainPercent = (decimal?)0.23m, Weight = 0.6667m } },
            UpcomingPayouts = new[] { new PortfolioManager.Core.Dashboard.ProjectedPayout("ASX", "CBA", new DateTime(2026, 12, 1), 210m) },
        };
        var site = new FakeSite(_ => (HttpStatusCode.OK, JsonSerializer.Serialize(written, AgentJson.Options)));

        var d = await new SiteApi(new HttpClient(site), "https://example.com", "k").GetDashboardAsync(4);

        Assert.Equal("https://example.com/api/v1/portfolios/4/dashboard", site.Requests[0].RequestUri!.ToString());
        Assert.Equal((15000m, 3000m, -120.5m), (d.TotalPortfolioValue, d.TotalCashValue, d.LatestMove!.Change));
        Assert.Equal(2000m, d.Holdings.CapitalGain);
        Assert.Equal(("CBA", 0.6667m), (d.TopHoldings[0].StockCode, d.TopHoldings[0].Weight));
        Assert.Equal(new DateTime(2026, 12, 1), Assert.Single(d.UpcomingPayouts).ProjectedPaymentDate);
    }

    [Fact]
    public async Task Api_reads_a_shares_price_history()
    {
        var written = new
        {
            Exchange = "ASX", Code = "BHP", Name = "BHP Group", Sector = "Materials", UnitsHeld = (decimal?)100m,
            Latest = (double?)45.2, LatestDate = (DateOnly?)new DateOnly(2026, 11, 10), Change = (double?)0.3, ChangePct = (double?)0.0067,
            YearLow = (double?)38.0, YearHigh = (double?)49.9,
            Prices = new[] { new { Date = new DateOnly(2026, 11, 10), Close = 45.2, Open = (double?)44.9, High = (double?)45.5, Low = (double?)44.8, Volume = (double?)1000 } },
        };
        var site = new FakeSite(_ => (HttpStatusCode.OK, JsonSerializer.Serialize(written, AgentJson.Options)));

        var h = await new SiteApi(new HttpClient(site), "https://example.com", "k").GetPriceHistoryAsync(4, "ASX", "BHP", new DateOnly(2026, 5, 1));

        Assert.Equal("https://example.com/api/v1/portfolios/4/prices/ASX/BHP?from=2026-05-01", site.Requests[0].RequestUri!.ToString());
        Assert.Equal(("BHP Group", 100m, 49.9), (h.Name, h.UnitsHeld, h.YearHigh));
        Assert.Equal(45.2, Assert.Single(h.Prices).Close);
    }

    [Fact]
    public async Task Api_reads_payouts_for_a_period()
    {
        var payout = new PortfolioManager.Core.Payouts.Payout(9, "ASX", "CBA", new DateTime(2026, 9, 28), 210m, 210m, 0, 90m, 0, 0, 0, 0, 0, 0, 0, "Final", true);
        var written = new
        {
            Totals = new PortfolioManager.Core.Payouts.PayoutTotals(210m, 210m, 0, 90m, 0, 0, 0, 0, 0),
            Payouts = new { Total = 1, Returned = 1, Rows = new[] { payout } },
        };
        var site = new FakeSite(_ => (HttpStatusCode.OK, JsonSerializer.Serialize(written, AgentJson.Options)));

        var list = await new SiteApi(new HttpClient(site), "https://example.com", "k").ListPayoutsAsync(4, new DateOnly(2026, 7, 1), new DateOnly(2027, 6, 30));

        Assert.Equal("https://example.com/api/v1/portfolios/4/payouts?from=2026-07-01&to=2027-06-30&limit=500", site.Requests[0].RequestUri!.ToString());
        Assert.Equal(90m, list.Totals!.FrankingCredit);
        var row = Assert.Single(list.Payouts.Rows);
        Assert.Equal((9, "CBA", 210m, "Final"), (row.PayoutId, row.StockCode, row.Total, row.Comment));
    }

    [Fact]
    public async Task Offline_copy_is_shown_when_the_site_cant_be_reached()
    {
        var folder = Path.Combine(Path.GetTempPath(), "pm-offline-" + Guid.NewGuid().ToString("N"));
        var key = OfflineCopy.NewKey();
        try
        {
            var up = new FakeSite(_ => (HttpStatusCode.OK, """[{"portfolioId":3,"name":"Sample","entityType":null,"role":null,"canEdit":true}]"""));
            var online = new SiteApi(new HttpClient(up), "https://example.com", "k", new OfflineCopy(folder, key));
            await online.ListPortfoliosAsync();
            Assert.Null(online.OfflineCopySaved);
            Assert.DoesNotContain(Directory.GetFiles(folder), f => File.ReadAllText(f).Contains("Sample"));

            var down = new FakeSite(_ => throw new HttpRequestException("no signal"));
            var offline = new SiteApi(new HttpClient(down), "https://example.com", "k", new OfflineCopy(folder, key));
            Assert.Equal("Sample", Assert.Single(await offline.ListPortfoliosAsync()).Name);
            Assert.NotNull(offline.OfflineCopySaved);

            // Not readable with another key, and nothing saved for another address
            var otherKey = new SiteApi(new HttpClient(down), "https://example.com", "k", new OfflineCopy(folder, OfflineCopy.NewKey()));
            Assert.True((await Assert.ThrowsAsync<SiteException>(() => otherKey.ListPortfoliosAsync())).Unreachable);
            await Assert.ThrowsAsync<SiteException>(() => offline.GetHoldingsAsync(3));

            // A refusal from the site is never covered up by the copy
            var revoked = new SiteApi(new HttpClient(new FakeSite(_ => (HttpStatusCode.Unauthorized, ""))), "https://example.com", "k", new OfflineCopy(folder, key));
            Assert.True((await Assert.ThrowsAsync<SiteException>(() => revoked.ListPortfoliosAsync())).SignedOut);

            OfflineCopy.Clear(folder);
            Assert.False(Directory.Exists(folder));
        }
        finally
        {
            OfflineCopy.Clear(folder);
        }
    }

    [Theory]
    [InlineData(0, "2026-11-11", "2025-11-12", "2026-11-11")]
    [InlineData(1, "2026-11-11", "2026-07-01", "2027-06-30")]
    [InlineData(2, "2026-11-11", "2025-07-01", "2026-06-30")]
    [InlineData(1, "2026-03-05", "2025-07-01", "2026-06-30")]
    [InlineData(2, "2026-07-01", "2025-07-01", "2026-06-30")]
    public void Payout_periods_follow_the_july_to_june_year(int choice, string today, string from, string to) =>
        Assert.Equal((DateOnly.Parse(from), DateOnly.Parse(to)), ReportPeriod.Range(choice, DateOnly.Parse(today)));

    [Fact]
    public async Task Trade_is_posted_with_the_names_add_trade_reads_and_gives_back_the_new_id()
    {
        var site = new FakeSite(_ => (HttpStatusCode.OK, """{"message":"Recorded the buy of 10 BHP at 45 AUD.","tradeId":812,"trade":{}}"""));
        var api = new SiteApi(new HttpClient(site), "https://example.com", "k");

        var saved = await api.AddTradeAsync(4, new TradeRequest(new DateOnly(2026, 11, 10), "Buy", "ASX", "BHP", 10, 45m, 9.5m, "AUD"));

        Assert.Equal(HttpMethod.Post, site.Requests[0].Method);
        Assert.Equal("https://example.com/api/v1/portfolios/4/trades", site.Requests[0].RequestUri!.ToString());
        Assert.Equal(812, saved.TradeId);
        var body = JsonDocument.Parse(site.Bodies[0]).RootElement;
        // Every name the app sends is one the tool's schema has
        var schema = new AgentTools().Find("add_trade")!.InputSchema["properties"]!.AsObject().Select(p => p.Key).ToHashSet();
        Assert.All(body.EnumerateObject(), p => Assert.Contains(p.Name, schema));
        Assert.Equal("2026-11-10", body.GetProperty("date").GetString());
        Assert.Equal(10m, body.GetProperty("quantity").GetDecimal());
    }

    [Theory]
    [InlineData("add_payout")]
    [InlineData("add_cash_transaction")]
    [InlineData("add_attachment")]
    public async Task Other_entries_send_only_names_their_tool_reads(string toolName)
    {
        var site = new FakeSite(_ => (HttpStatusCode.OK, """{"message":"Saved.","attachmentId":5}"""));
        var api = new SiteApi(new HttpClient(site), "https://example.com", "k");
        var day = new DateOnly(2026, 9, 28);
        var saved = toolName switch
        {
            "add_payout" => await api.AddPayoutAsync(4, new PayoutRequest(day, "ASX", "CBA", 210m, 210m, 0, 90m)),
            "add_cash_transaction" => await api.AddCashAsync(4, new CashRequest(day, "Deposit", 500m, "Top up")),
            _ => await api.AddAttachmentAsync(4, new AttachmentRequest("Trade", 812, "note.jpg", "AAAA")),
        };
        Assert.Equal("Saved.", saved.Message);
        var schema = new AgentTools().Find(toolName)!.InputSchema["properties"]!.AsObject().Select(p => p.Key).ToHashSet();
        Assert.All(JsonDocument.Parse(site.Bodies[0]).RootElement.EnumerateObject(), p => Assert.Contains(p.Name, schema));
    }

    [Fact]
    public async Task A_write_that_times_out_says_it_may_have_been_saved_and_is_never_taken_from_the_offline_copy()
    {
        var slow = new FakeSite(_ => throw new TaskCanceledException("timeout"));
        var e = await Assert.ThrowsAsync<SiteException>(() => new SiteApi(new HttpClient(slow), "https://example.com", "k")
            .AddCashAsync(1, new CashRequest(new DateOnly(2026, 1, 1), "Deposit", 1m)));
        Assert.Contains("may or may not have been saved", e.Message);

        var refused = new FakeSite(_ => (HttpStatusCode.BadRequest, """{"error":"amount must be more than zero."}"""));
        e = await Assert.ThrowsAsync<SiteException>(() => new SiteApi(new HttpClient(refused), "https://example.com", "k")
            .AddCashAsync(1, new CashRequest(new DateOnly(2026, 1, 1), "Deposit", 1m)));
        Assert.Equal("amount must be more than zero.", e.Message);

        // A success status without the site's message doesn't confirm the save
        foreach (var odd in new[] { "", "{}", "not json" })
        {
            var unclear = new FakeSite(_ => (HttpStatusCode.OK, odd));
            e = await Assert.ThrowsAsync<SiteException>(() => new SiteApi(new HttpClient(unclear), "https://example.com", "k")
                .AddCashAsync(1, new CashRequest(new DateOnly(2026, 1, 1), "Deposit", 1m)));
            Assert.Contains("may or may not have been saved", e.Message);
        }
    }

    [Fact]
    public async Task Payout_form_can_list_shares_already_sold()
    {
        var site = new FakeSite(_ => (HttpStatusCode.OK, """{"holdings":[],"total":{"marketValue":0,"capitalGain":0,"payouts":0,"totalProfit":0,"cost":0}}"""));
        await new SiteApi(new HttpClient(site), "https://example.com", "k").GetHoldingsAsync(4, openOnly: false);
        Assert.Equal("https://example.com/api/v1/portfolios/4/holdings?openOnly=false", site.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public void Entry_checks_catch_simple_slips()
    {
        var today = new DateOnly(2026, 11, 11);
        Assert.Empty(EntryChecks.Trade(new TradeRequest(today, "Buy", "ASX", "BHP", 10, 45m, 0, "AUD"), today));
        Assert.Contains("Units must be a whole number above zero.", EntryChecks.Trade(new TradeRequest(today, "Buy", "ASX", "BHP", 1.5m, 45m, 0, "AUD"), today));
        Assert.Contains("The trade date can't be in the future.", EntryChecks.Trade(new TradeRequest(today.AddDays(1), "Buy", "ASX", "BHP", 1, 45m, 0, "AUD"), today));
        Assert.Contains("The franked part must be between zero and the cash received.", EntryChecks.Payout(new PayoutRequest(today, "ASX", "CBA", 100m, 120m, 0, 0), today));
        Assert.Empty(EntryChecks.Cash(new CashRequest(today, "Withdrawal", 5m)));
        Assert.Equal(42.86m, EntryChecks.FrankingCredit(100m, 0.30m));
        Assert.Equal(33.33m, EntryChecks.FrankingCredit(100m, 0.25m));
    }

    private sealed class FakeSite(Func<HttpRequestMessage, (HttpStatusCode Status, string Body)> reply) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            var (status, body) = reply(request);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
