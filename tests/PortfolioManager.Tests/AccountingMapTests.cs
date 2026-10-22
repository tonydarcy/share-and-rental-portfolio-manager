using PortfolioManager.Core.Accounting;

namespace PortfolioManager.Tests;

public class AccountingMapTests
{
    private static Dictionary<string, object?> Row(params (string Key, object? Value)[] values) =>
        values.ToDictionary(v => v.Key, v => v.Value);

    private static LedgerAccount Account(int id, int number, string name, int owner = 7) =>
        new(id, number, name, "Expense", "AUD", owner, "", null, true);

    [Fact]
    public void Maps_the_chart_of_accounts_before_and_after_the_migration()
    {
        var accounts = ChartOfAccountsService.Map([
            Row(("iAccountID", 1), ("iAccountNumber", 1000), ("strAccountName", "Cash at Bank "), ("strAccountType", "Asset"),
                ("strCurrency", "AUD"), ("iUserID", 0), ("ParentAccountName", "-")),
            Row(("iAccountID", 2), ("iAccountNumber", 6100), ("strAccountName", "Bank Fees"), ("strAccountType", "Expense"),
                ("strCurrency", DBNull.Value), ("iUserID", 7), ("ParentAccountName", "Expenses"), ("iParentAccountID", 5), ("bIsPostable", false)),
        ]);

        Assert.Equal(2, accounts.Count);
        Assert.True(accounts[0].IsSystem);
        Assert.Equal("Cash at Bank", accounts[0].Name);
        Assert.Equal("", accounts[0].ParentName);
        Assert.Null(accounts[0].Postable);
        Assert.False(accounts[0].IsInactive);
        Assert.Equal("6100 - Bank Fees", accounts[1].Display);
        Assert.Null(accounts[1].Currency);
        Assert.Equal(5, accounts[1].ParentId);
        Assert.True(accounts[1].IsInactive);
    }

    [Fact]
    public void Only_the_portfolios_own_accounts_can_be_changed()
    {
        IReadOnlyList<LedgerAccount> accounts = [Account(1, 1000, "Cash", owner: 0), Account(2, 6100, "Fees"), Account(3, 6200, "Other", owner: 8)];

        Assert.Null(ChartOfAccountsService.OwnAccount(accounts, 7, 2));
        Assert.Contains("System", ChartOfAccountsService.OwnAccount(accounts, 7, 1)!.Error);
        Assert.Contains("wasn't found", ChartOfAccountsService.OwnAccount(accounts, 7, 3)!.Error);
        Assert.Contains("wasn't found", ChartOfAccountsService.OwnAccount(accounts, 7, 99)!.Error);
    }

    [Theory]
    [InlineData("Success", null)]
    [InlineData(" success ", null)]
    [InlineData("Error: Account number already exists.", "Account number already exists.")]
    [InlineData("Something odd", "Something odd")]
    [InlineData(null, "The change wasn't saved.")]
    public void Reads_the_proc_result(string? text, string? error)
    {
        Assert.Equal(error, ChartOfAccountsService.Result(text).Error);
        Assert.Equal(error, BankImportService.Result(text).Error);
    }

    [Fact]
    public void Validates_the_account_form()
    {
        IReadOnlyList<LedgerAccount> accounts = [Account(1, 1000, "Cash", owner: 0), Account(2, 6100, "Bank Fees")];

        Assert.Empty(new AccountInput { Number = 6200, Name = "Accounting Fees", Type = "Expense", ParentId = 2 }.Validate(accounts));
        // Editing an account may keep its own number and name
        Assert.Empty(new AccountInput { Number = 6100, Name = "bank fees", Type = "Expense" }.Validate(accounts, editingId: 2));

        var errors = new AccountInput { Number = 1000, Name = " BANK FEES ", Type = "Stuff", ParentId = 42 }.Validate(accounts);
        Assert.Contains("Account number 1000 is already used.", errors);
        Assert.Contains("There is already an account called \"BANK FEES\".", errors);
        Assert.Contains("Choose the account type.", errors);
        Assert.Contains("Choose a parent account from the list.", errors);

        errors = new AccountInput { Number = 0, Name = "", ParentId = 2 }.Validate(accounts, editingId: 2);
        Assert.Contains("Enter an account number from 1 to 999999.", errors);
        Assert.Contains("Enter the account name.", errors);
        Assert.Contains("An account can't sit under itself.", errors);
    }

    [Theory]
    [InlineData("Hybrid", true)]
    [InlineData("DoubleEntry ", true)]
    [InlineData("SingleEntry", false)]
    [InlineData(null, false)]
    public void Only_hybrid_and_double_entry_post_journals(string? mode, bool posts) =>
        Assert.Equal(posts, AccountingMode.PostsJournals(mode));

    [Fact]
    public void Maps_account_choices_and_skips_blank_rows() =>
        Assert.Equal([new AccountChoice(4, "1100 - Bank")],
            ChartOfAccountsService.MapChoices([Row(("iAccountID", 4), ("DisplayName", "1100 - Bank")), Row(("DisplayName", "x"))]));

    [Fact]
    public void A_journal_of_another_portfolio_is_not_returned()
    {
        var header = Row(("iJournalID", 9), ("iUserID", 8), ("dtmTransactionDate", new DateTime(2026, 6, 30)), ("strSourceModule", "ManualJournal"));
        Assert.Null(JournalService.MapDetail(header, [], 7));
        Assert.Null(JournalService.MapDetail(null, [], 7));
    }

    [Fact]
    public void Journal_detail_keeps_only_its_own_lines_debits_first()
    {
        var header = Row(("iJournalID", 9), ("iUserID", 7), ("dtmTransactionDate", new DateTime(2026, 6, 30)),
            ("strDescription", "Depreciation"), ("strSourceModule", "ManualJournal"), ("strBatchID", ""), ("dtmCreatedDate", new DateTime(2026, 7, 1, 9, 30, 0)));
        var journal = JournalService.MapDetail(header, [
            Row(("iJournalLineID", 1), ("iJournalID", 9), ("iAccountID", 20), ("strAccountName", "Accumulated"), ("dblCredit", 50.0), ("dblDebit", 0.0)),
            Row(("iJournalLineID", 2), ("iJournalID", 9), ("iAccountID", 10), ("strAccountName", "Depreciation"), ("dblDebit", 50.0), ("strDescription", "Depreciation")),
            Row(("iJournalLineID", 3), ("iJournalID", 10), ("iAccountID", 30), ("dblDebit", 999.0)),
        ], 7)!;

        Assert.True(journal.IsManual);
        Assert.Null(journal.BatchId);
        Assert.Equal(new DateOnly(2026, 6, 30), journal.Date);
        Assert.Equal([2, 1], journal.Lines.Select(l => l.LineId));
        Assert.Equal(50m, journal.TotalDebit);
        Assert.Equal(50m, journal.TotalCredit);

        var input = JournalService.ToInput(journal);
        Assert.Equal(3, input.Lines.Count);
        Assert.Null(input.Lines[0].Description); // same as the journal's, so left blank
        Assert.Null(input.Lines[0].Credit);
        Assert.Equal(50m, input.Lines[1].Credit);
        Assert.True(input.Lines[2].IsBlank);
    }

    [Fact]
    public void Maps_the_journal_list()
    {
        var list = JournalService.MapList([
            Row(("iJournalID", 3), ("dtmTransactionDate", new DateTime(2026, 1, 2)), ("strDescription", "Buy BHP"), ("strSourceModule", "ShareTrade"), ("TotalAmount", 1234.5m)),
            Row(("iJournalID", DBNull.Value)),
        ]);

        var journal = Assert.Single(list);
        Assert.False(journal.IsManual);
        Assert.Equal(1234.5m, journal.TotalAmount);
    }

    [Fact]
    public void Profit_and_loss_totals_come_from_the_total_rows_and_a_null_total_is_zero()
    {
        var report = LedgerReportService.MapProfitLoss([
            Row(("Label", "Revenue"), ("RowType", "Header"), ("Amount", DBNull.Value)),
            Row(("Label", "Dividends"), ("RowType", "Item"), ("Amount", 300m), ("IndentLevel", 1)),
            Row(("Label", "Total Revenue"), ("RowType", "Total"), ("Amount", 300m)),
            Row(("Label", "Total Expenses"), ("RowType", "Total"), ("Amount", DBNull.Value)),
            Row(("Label", "Net Profit"), ("RowType", "GrandTotal"), ("Amount", 300m)),
        ]);

        Assert.Equal(300m, report.Revenue);
        Assert.Equal(0m, report.Expenses);
        Assert.Equal(300m, report.NetProfit);
        Assert.Null(report.Lines[0].Amount);
        Assert.Equal(0m, report.Lines[3].Amount);
        Assert.Equal(1, report.Lines[1].Indent);
    }

    [Fact]
    public void Balance_sheet_totals_come_from_their_sections()
    {
        var report = LedgerReportService.MapBalanceSheet([
            Row(("AccountName", "Total Assets"), ("RowType", "Total"), ("AccountType", "Asset"), ("Amount", 1000m)),
            Row(("AccountName", "Total Liabilities"), ("RowType", "Total"), ("AccountType", "Liability"), ("Amount", 200m)),
            Row(("AccountName", "Net Assets"), ("RowType", "GrandTotal"), ("AccountType", "NetAssets"), ("Amount", 800m)),
            Row(("AccountName", "Total Equity"), ("RowType", "GrandTotal"), ("AccountType", "Equity"), ("Amount", 790m)),
        ]);

        Assert.Equal("Total Assets", report.Lines[0].Label);
        Assert.Equal((1000m, 200m, 800m, 790m), (report.Assets, report.Liabilities, report.NetAssets, report.Equity));
    }

    [Fact]
    public void Cash_flow_totals_come_from_their_labels()
    {
        var report = LedgerReportService.MapCashFlow([
            Row(("Label", "Net Profit"), ("RowType", "GrandTotal"), ("Amount", 50m)),
            Row(("Label", "Total Cash Movement"), ("RowType", "GrandTotal"), ("Amount", 40m)),
            Row(("Label", "Total Change in Bank Balances"), ("RowType", "Total"), ("Amount", 40m)),
        ]);

        Assert.Equal((50m, 40m, 40m), (report.NetProfit, report.TotalCashMovement, report.ChangeInBank));
    }

    [Fact]
    public void Trial_balance_totals_come_from_the_footer_or_the_items()
    {
        var withFooter = LedgerReportService.MapTrialBalance([
            Row(("GroupName", "Assets"), ("AccountName", "Assets"), ("RowType", "Header")),
            Row(("GroupName", "Assets"), ("AccountNumber", 1000), ("AccountName", "Cash"), ("RowType", "Item"), ("Debit", 100m), ("Credit", DBNull.Value)),
            Row(("GroupName", "Equity"), ("AccountNumber", 3100), ("AccountName", "Capital"), ("RowType", "Item"), ("Credit", 100m)),
            Row(("GroupName", "Footer"), ("AccountName", "TRIAL BALANCE TOTAL"), ("RowType", "GrandTotal"), ("Debit", 100m), ("Credit", 100m)),
        ]);
        Assert.Null(withFooter.Lines[0].Debit);
        Assert.Equal(0m, withFooter.Lines[1].Credit);
        Assert.Equal((100m, 100m), (withFooter.TotalDebit, withFooter.TotalCredit));

        var noFooter = LedgerReportService.MapTrialBalance([
            Row(("AccountName", "Cash"), ("Debit", 60m)),
            Row(("AccountName", "Capital"), ("Credit", 50m)),
        ]);
        Assert.Equal("Item", noFooter.Lines[0].RowType);
        Assert.Equal((60m, 50m), (noFooter.TotalDebit, noFooter.TotalCredit));
    }

    [Fact]
    public void Import_history_counts_each_transaction_once()
    {
        var batch = Assert.Single(BankImportService.MapHistory([
            Row(("strBatchID", "abc"), ("ImportDate", new DateTime(2026, 10, 1)), ("TransactionCount", 10), ("AllocatedCount", 2), ("TotalValue", 99.5m)),
            Row(("strBatchID", DBNull.Value), ("TransactionCount", 4)),
        ]));

        Assert.Equal(5, batch.Transactions);
        Assert.Equal(40, batch.Percent);
        Assert.True(batch.Locked);
        Assert.False(batch.FullyAllocated);
    }

    [Fact]
    public void Unallocated_lines_skip_matched_ones()
    {
        var line = Assert.Single(BankImportService.MapUnallocated([
            Row(("iJournalLineID", 5), ("dtmTransactionDate", new DateTime(2026, 9, 1)), ("strDescription", "Dividend"), ("Amount", 120m), ("iMatchedJournalID", DBNull.Value)),
            Row(("iJournalLineID", 6), ("dtmTransactionDate", new DateTime(2026, 9, 1)), ("Amount", -5m), ("iMatchedJournalID", 77)),
        ]));

        Assert.True(line.IsDeposit);
        Assert.Equal(new DateOnly(2026, 9, 1), line.Date);
    }

    [Fact]
    public void Match_descriptions_name_what_was_matched() =>
        Assert.Equal("Matched to payout 12: BHP dividend", BankImportService.MatchDescription("payout", 12, "BHP dividend"));
}
