using PortfolioManager.Core.Accounting;

namespace PortfolioManager.Tests;

public class DoubleEntryConversionTests
{
    private static DoubleEntryConversionPreview Preview(string mode = "SingleEntry", int payouts = 3, int cash = 2,
        DateOnly? earliest = null, DateOnly? lockedTo = null, int bankImports = 0) =>
        new(mode, true, 5, cash, payouts, 1, 0, earliest, lockedTo, bankImports, 0);

    [Fact]
    public void Parameters_name_the_signed_in_user_and_the_portfolio()
    {
        var p = DoubleEntryConversionService.Parameters(7, 42);
        Assert.Equal(7, p.GetType().GetProperty("iUserId")!.GetValue(p));
        Assert.Equal(42, p.GetType().GetProperty("iPortfolioId")!.GetValue(p));
    }

    [Fact]
    public void The_preview_maps_from_the_proc_row()
    {
        var p = DoubleEntryConversionService.MapPreview(new Dictionary<string, object?>
        {
            ["strAccountingMode"] = "SingleEntry ", ["bCanSwitch"] = true, ["iTrades"] = 10, ["iCash"] = 4, ["iPayouts"] = 6,
            ["iCorporateActions"] = 1, ["iOther"] = 2, ["dtmEarliest"] = new DateTime(2019, 8, 1), ["dtmLockedTo"] = DBNull.Value,
            ["iBankImportJournals"] = 3, ["iManualJournals"] = 1,
        });
        Assert.Equal("SingleEntry", p.Mode);
        Assert.True(p.CanSwitch);
        Assert.Equal(23, p.Records);
        Assert.Equal(new DateOnly(2019, 8, 1), p.Earliest);
        Assert.Null(p.LockedTo);
        Assert.False(p.PostsJournals);
        Assert.True(p.MayDoubleUp);
    }

    [Fact]
    public void A_missing_mode_reads_as_single_entry() =>
        Assert.Equal("SingleEntry", DoubleEntryConversionService.MapPreview(new Dictionary<string, object?>()).Mode);

    [Theory]
    [InlineData("SingleEntry", false)]
    [InlineData("Hybrid", true)]
    [InlineData("DoubleEntry", true)]
    public void Only_single_entry_needs_switching(string mode, bool posts) =>
        Assert.Equal(posts, Preview(mode).PostsJournals);

    [Fact]
    public void Records_dated_in_a_locked_period_stop_the_switch()
    {
        Assert.True(Preview(earliest: new(2020, 1, 1), lockedTo: new(2020, 6, 30)).Locked);
        Assert.False(Preview(earliest: new(2020, 7, 1), lockedTo: new(2020, 6, 30)).Locked);
        Assert.False(Preview(earliest: new(2020, 1, 1)).Locked);
    }

    [Fact]
    public void Bank_imports_warn_only_when_payouts_or_cash_will_post()
    {
        Assert.True(Preview(bankImports: 2).MayDoubleUp);
        Assert.False(Preview(bankImports: 0).MayDoubleUp);
        Assert.False(Preview(bankImports: 2, payouts: 0, cash: 0).MayDoubleUp);
    }
}
