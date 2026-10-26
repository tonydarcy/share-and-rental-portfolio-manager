using System.Text.Json;
using PortfolioManager.Core.Exports;

namespace PortfolioManager.Tests;

public class AccountingLinkTests
{
    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;

    private static readonly ExportJournal Journal = new("Dividend BHP", new DateOnly(2026, 3, 20),
        [("090", "BAS Excluded", "Cash", 100m), ("260", "BASEXCLUDED", "Dividend", -100m)]);

    [Fact]
    public void The_sign_in_address_carries_the_app_and_the_state()
    {
        var url = AccountingApi.AuthorizeUrl(ExportSystems.Xero, new AppRegistration { ClientId = "abc", Scopes = "offline_access accounting.manualjournals" },
            "https://example.com/Accounting/Connected", "s t");
        Assert.StartsWith(AccountingApi.XeroAuthorize + "?", url);
        Assert.Contains("client_id=abc", url);
        Assert.Contains("scope=offline_access%20accounting.manualjournals", url);
        Assert.Contains("state=s%20t", url);
    }

    [Fact]
    public void Tokens_keep_the_old_refresh_token_when_none_comes_back()
    {
        var now = new DateTime(2026, 10, 26, 0, 0, 0, DateTimeKind.Utc);
        var t = AccountingApi.ReadTokens(Json("""{"access_token":"a","expires_in":"1200"}"""), now, "old");
        Assert.NotNull(t);
        Assert.Equal("old", t.RefreshToken);
        Assert.Equal(now.AddMinutes(20), t.ExpiresUtc);
        Assert.Equal(t, AccountingApi.LoadTokens(AccountingApi.SaveTokens(t)));
        Assert.Null(AccountingApi.LoadTokens("not json"));
        Assert.Null(AccountingApi.ReadTokens(Json("""{"error":"invalid_grant"}"""), now));
    }

    [Fact]
    public void Xero_journals_go_as_drafts_with_tax_types_found_by_name_or_code()
    {
        var types = AccountingApi.XeroTaxTypes(Json("""{"TaxRates":[{"Name":"BAS Excluded","TaxType":"BASEXCLUDED"}]}"""));
        var (json, missing) = AccountingApi.XeroManualJournals([Journal], types);
        Assert.Empty(missing);
        var j = Json(json).GetProperty("ManualJournals")[0];
        Assert.Equal("DRAFT", j.GetProperty("Status").GetString());
        Assert.Equal("2026-03-20", j.GetProperty("Date").GetString());
        Assert.All(j.GetProperty("JournalLines").EnumerateArray(), l => Assert.Equal("BASEXCLUDED", l.GetProperty("TaxType").GetString()));

        Assert.Equal(["BAS Excluded", "BASEXCLUDED"], AccountingApi.XeroManualJournals([Journal], new Dictionary<string, string>()).Missing);
    }

    [Fact]
    public void Myob_lines_are_positive_amounts_with_a_credit_flag()
    {
        var accounts = AccountingApi.MyobUids(Json("""{"Items":[{"DisplayID":"090","UID":"u1"},{"DisplayID":"260","UID":"u2"}]}"""), "DisplayID");
        var codes = new Dictionary<string, string> { ["BAS Excluded"] = "t1", ["BASEXCLUDED"] = "t1" };
        var (json, missing) = AccountingApi.MyobGeneralJournal(Journal, accounts, codes);
        Assert.Empty(missing);
        var lines = Json(json).GetProperty("Lines");
        Assert.Equal(100m, lines[1].GetProperty("Amount").GetDecimal());
        Assert.True(lines[1].GetProperty("IsCredit").GetBoolean());
        Assert.False(lines[0].GetProperty("IsCredit").GetBoolean());
        Assert.Equal(["account 260"], AccountingApi.MyobGeneralJournal(Journal, new Dictionary<string, string> { ["090"] = "u1" }, codes).Missing);
    }

    [Fact]
    public void Errors_are_read_out_of_the_answer_and_sends_warn_on_overlapping_dates()
    {
        Assert.Contains("Account code 999 is not valid", AccountingApi.ErrorText("""{"Elements":[{"ValidationErrors":[{"Message":"Account code 999 is not valid"}]}]}""", 400));
        var send = new AccountingSend("Xero", "Journals", new DateOnly(2025, 7, 1), new DateOnly(2026, 6, 30), 10, 10, null, DateTime.Today, null);
        Assert.True(send.Overlaps(new DateOnly(2026, 6, 1), new DateOnly(2026, 7, 31)));
        Assert.False(send.Overlaps(new DateOnly(2026, 7, 1), new DateOnly(2026, 9, 30)));
        Assert.True(send.Overlaps(null, new DateOnly(2026, 9, 30)));
    }
}
