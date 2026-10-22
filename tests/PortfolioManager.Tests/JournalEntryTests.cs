using PortfolioManager.Core.Accounting;

namespace PortfolioManager.Tests;

public class JournalEntryTests
{
    private static readonly IReadOnlySet<int> Allowed = new HashSet<int> { 10, 20, 30 };

    private static JournalInput Journal(params JournalLineInput[] lines) => new()
    {
        Date = new DateTime(2026, 6, 30),
        Description = "Year end depreciation",
        Lines = [.. lines],
    };

    private static JournalLineInput Dr(int account, decimal amount) => new() { AccountId = account, Debit = amount };
    private static JournalLineInput Cr(int account, decimal amount) => new() { AccountId = account, Credit = amount };

    [Fact]
    public void A_balanced_journal_is_valid_and_blank_rows_are_ignored()
    {
        var journal = Journal(Dr(10, 100.25m), new JournalLineInput(), Cr(20, 60m), Cr(30, 40.25m), new JournalLineInput());

        Assert.Empty(journal.Validate(Allowed));
        Assert.Equal(3, journal.UsedLines.Count);
        Assert.Equal(100.25m, journal.TotalDebit);
        Assert.Equal(100.25m, journal.TotalCredit);
    }

    [Fact]
    public void An_unbalanced_journal_says_by_how_much()
    {
        var errors = Journal(Dr(10, 100m), Cr(20, 99.99m)).Validate(Allowed);

        var error = Assert.Single(errors);
        Assert.Contains("out of balance", error);
        Assert.Contains("0.01", error);
    }

    [Fact]
    public void Needs_two_lines()
    {
        Assert.Contains(Journal(Dr(10, 5m)).Validate(Allowed), e => e.Contains("at least two lines"));
        Assert.Contains(Journal().Validate(Allowed), e => e.Contains("at least two lines"));
    }

    [Fact]
    public void Each_line_is_a_debit_or_a_credit_on_an_allowed_account()
    {
        var errors = Journal(
            new JournalLineInput { AccountId = 10, Debit = 5m, Credit = 5m },
            new JournalLineInput { AccountId = 10 , Description = "nothing" },
            new JournalLineInput { AccountId = 99, Debit = 5m },
            new JournalLineInput { Credit = 5m },
            new JournalLineInput { AccountId = 20, Debit = -5m },
            new JournalLineInput { AccountId = 20, Credit = 1.005m }).Validate(Allowed);

        Assert.Contains("Line 1: enter either a debit or a credit, not both.", errors);
        Assert.Contains("Line 2: enter a debit or a credit amount.", errors);
        Assert.Contains("Line 3: that account can't be posted to in this portfolio.", errors);
        Assert.Contains("Line 4: choose an account.", errors);
        Assert.Contains("Line 5: amounts can't be negative.", errors);
        Assert.Contains("Line 6: amounts are in dollars and cents (at most 2 decimal places).", errors);
        // The balance isn't checked until the lines themselves are right
        Assert.DoesNotContain(errors, e => e.Contains("out of balance"));
    }

    [Fact]
    public void Needs_a_description_and_a_sensible_date()
    {
        var journal = Journal(Dr(10, 1m), Cr(20, 1m));
        journal.Description = "  ";
        journal.Date = new DateTime(1800, 1, 1);

        var errors = journal.Validate(Allowed);

        Assert.Contains("Enter a description for the journal.", errors);
        Assert.Contains("Enter a valid journal date.", errors);
    }

    [Fact]
    public void A_new_journal_starts_with_two_empty_rows()
    {
        var journal = JournalInput.New(new DateTime(2026, 10, 15));
        Assert.Equal(2, journal.Lines.Count);
        Assert.Empty(journal.UsedLines);
    }

    [Theory]
    [InlineData("ManualJournal", true)]
    [InlineData(" manualjournal ", true)]
    [InlineData("ManualAllocation", false)]
    [InlineData("Payout", false)]
    [InlineData("ShareTrade", false)]
    [InlineData("BankTransaction", false)]
    [InlineData("BankImport", false)]
    [InlineData(null, false)]
    public void Only_manual_journals_can_be_changed(string? source, bool manual) =>
        Assert.Equal(manual, JournalRules.IsManual(source));

    [Theory]
    [InlineData("ShareTrade", "Share trade")]
    [InlineData("BankTransaction", "Cash transaction")]
    [InlineData("ManualAllocation", "Bank allocation")]
    [InlineData("Something", "Something")]
    [InlineData(null, "Unknown")]
    public void Names_where_a_journal_came_from(string? source, string label) =>
        Assert.Equal(label, JournalRules.SourceLabel(source));
}
