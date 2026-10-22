using PortfolioManager.Core.Accounting;

namespace PortfolioManager.Tests;

public class RecurringJournalTests
{
    private static RecurringJournal Make(string frequency, DateOnly start, int posted = 0, DateOnly? end = null, bool active = true) =>
        new(1, "Rent", "Office rent", frequency, start, end, posted, false, active,
            [new JournalLineInput { AccountId = 10, Debit = 100 }, new JournalLineInput { AccountId = 20, Credit = 100 }], null);

    [Fact]
    public void Monthly_dates_count_from_the_start_so_month_ends_stay_month_ends()
    {
        var start = new DateOnly(2026, 1, 31);
        Assert.Equal(new DateOnly(2026, 2, 28), RepeatFrequencies.Occurrence(start, RepeatFrequencies.Monthly, 1));
        Assert.Equal(new DateOnly(2026, 3, 31), RepeatFrequencies.Occurrence(start, RepeatFrequencies.Monthly, 2));
    }

    [Theory]
    [InlineData(RepeatFrequencies.Weekly, 2026, 1, 15)]
    [InlineData(RepeatFrequencies.Fortnightly, 2026, 1, 29)]
    [InlineData(RepeatFrequencies.Quarterly, 2026, 7, 1)]
    [InlineData(RepeatFrequencies.HalfYearly, 2027, 1, 1)]
    [InlineData(RepeatFrequencies.Yearly, 2028, 1, 1)]
    public void Second_occurrence_after_the_start(string frequency, int y, int m, int d) =>
        Assert.Equal(new DateOnly(y, m, d), RepeatFrequencies.Occurrence(new DateOnly(2026, 1, 1), frequency, 2));

    [Fact]
    public void Due_dates_stop_at_today_and_the_end_date()
    {
        var r = Make(RepeatFrequencies.Monthly, new DateOnly(2026, 1, 1), posted: 1, end: new DateOnly(2026, 4, 1));
        Assert.Equal([new DateOnly(2026, 2, 1), new DateOnly(2026, 3, 1)], r.DueDates(new DateOnly(2026, 3, 15)));
        Assert.Equal(3, r.DueDates(new DateOnly(2027, 1, 1)).Count);
        Assert.Equal(new DateOnly(2026, 2, 1), r.NextDate);
        Assert.False(r.Finished);
        Assert.True((r with { PostedCount = 4 }).Finished);
    }

    [Fact]
    public void Paused_journals_have_nothing_due() =>
        Assert.Empty(Make(RepeatFrequencies.Weekly, new DateOnly(2026, 1, 1), active: false).DueDates(new DateOnly(2026, 6, 1)));

    [Fact]
    public void Reversal_swaps_debits_and_credits()
    {
        var original = new JournalInput
        {
            Description = "Accrued fees",
            Lines = [new JournalLineInput { AccountId = 10, Debit = 250, TaxCodeId = 3 }, new JournalLineInput { AccountId = 20, Credit = 250 }, new JournalLineInput()],
        };
        var r = JournalReversal.Reverse(original, new DateOnly(2026, 7, 1));
        Assert.Equal("Reversal of Accrued fees", r.Description);
        Assert.Equal(new DateTime(2026, 7, 1), r.Date);
        Assert.Equal(2, r.Lines.Count);
        Assert.Equal(250, r.Lines[0].Credit);
        Assert.Null(r.Lines[0].Debit);
        Assert.Equal(3, r.Lines[0].TaxCodeId);
        Assert.Equal(250, r.Lines[1].Debit);
    }

    [Fact]
    public void Lines_survive_the_xml_round_trip()
    {
        var lines = new[]
        {
            new JournalLineInput { AccountId = 10, Debit = 1234.56m, Description = "Rent & outgoings", TaxCodeId = 2, Tax = 112.23m },
            new JournalLineInput { AccountId = 20, Credit = 1234.56m },
            new JournalLineInput(),
        };
        var back = JournalReversal.ParseLines(JournalReversal.LinesXml(lines));
        Assert.Equal(2, back.Count);
        Assert.Equal(1234.56m, back[0].Debit);
        Assert.Equal("Rent & outgoings", back[0].Description);
        Assert.Equal(112.23m, back[0].Tax);
        Assert.Equal(1234.56m, back[1].Credit);
        Assert.Null(back[1].Debit);
    }

    [Fact]
    public void Input_checks_the_frequency_and_dates()
    {
        var input = new RecurringJournalInput { Frequency = "Daily", End = new DateTime(2025, 1, 1) };
        Assert.Equal(2, input.Validate(new DateOnly(2026, 1, 1)).Count);
    }
}
