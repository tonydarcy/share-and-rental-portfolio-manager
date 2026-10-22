using System.Text;
using PortfolioManager.Core.Accounting;

namespace PortfolioManager.Tests;

public class BankStatementParserTests
{
    [Fact]
    public void Reads_a_qif_statement_with_payee_memo_and_category()
    {
        var statement = BankStatementParser.Parse("""
            !Type:Bank
            D15/10/2026
            T-1,234.56
            PWOOLWORTHS 1234
            MGroceries
            ^
            D16/10/2026
            T2500.00
            PSALARY
            LIncome
            ^
            """, "statement.qif");

        Assert.Null(statement.FileError);
        Assert.Equal("QIF", statement.Format);
        Assert.Equal(2, statement.ValidCount);
        var first = statement.Lines[0].Transaction!;
        Assert.Equal(new DateOnly(2026, 10, 15), first.Date);
        Assert.Equal(-1234.56m, first.Amount);
        Assert.Equal("WOOLWORTHS 1234 / Groceries", first.Description);
        Assert.Equal("SALARY / Income", statement.Lines[1].Transaction!.Description);
        Assert.True(statement.Lines[1].Transaction!.IsDeposit);
    }

    [Fact]
    public void Skips_the_account_block_and_a_last_record_without_a_caret()
    {
        var statement = BankStatementParser.ParseQif("""
            !Account
            NEveryday Account
            TBank
            ^
            !Type:Bank
            D01/07/2025
            U-10.00
            PFee
            """);

        var line = Assert.Single(statement.Lines);
        Assert.Equal(-10m, line.Transaction!.Amount);
        Assert.Equal(new DateOnly(2025, 7, 1), line.Transaction.Date);
    }

    [Fact]
    public void A_qif_record_with_a_bad_date_or_amount_is_an_error_not_skipped()
    {
        var statement = BankStatementParser.ParseQif("""
            !Type:Bank
            D31/02/2026
            T-5.00
            ^
            D01/03/2026
            Tabc
            ^
            D02/03/2026
            T0.00
            ^
            """);

        Assert.Equal(3, statement.ErrorCount);
        Assert.Contains("31/02/2026", statement.Lines[0].Error);
        Assert.Contains("isn't a number", statement.Lines[1].Error);
        Assert.Contains("zero", statement.Lines[2].Error);
    }

    [Theory]
    [InlineData("15/10/2026", 2026, 10, 15)]
    [InlineData("5/1/26", 2026, 1, 5)]
    [InlineData("2026-10-15", 2026, 10, 15)]
    [InlineData("15-Oct-2026", 2026, 10, 15)]
    [InlineData("15 Oct 2026", 2026, 10, 15)]
    [InlineData("20261015", 2026, 10, 15)]
    [InlineData("10/25'26", 2026, 10, 25)] // Quicken US style: impossible day first, so month first
    [InlineData(" 1/ 5'26", 2026, 5, 1)]
    public void Reads_day_first_dates_with_a_us_fallback(string text, int y, int m, int d) =>
        Assert.Equal(new DateOnly(y, m, d), BankStatementParser.ParseDate(text));

    [Theory]
    [InlineData("13/13/2026")]
    [InlineData("yesterday")]
    [InlineData("")]
    public void Rejects_dates_it_cannot_read(string text) => Assert.Null(BankStatementParser.ParseDate(text));

    [Theory]
    [InlineData("-1,234.56", -1234.56)]
    [InlineData("$50.00", 50)]
    [InlineData("50.00-", -50)]
    [InlineData("(12.50)", -12.5)]
    [InlineData("-50,25", -50.25)]
    [InlineData("+7", 7)]
    public void Reads_amounts(string text, double expected) =>
        Assert.Equal((decimal)expected, BankStatementParser.ParseAmount(text));

    [Theory]
    [InlineData("1.234")] // more than cents
    [InlineData("12-34")]
    [InlineData("")]
    public void Rejects_amounts_it_cannot_read(string text) => Assert.Null(BankStatementParser.ParseAmount(text));

    [Fact]
    public void Reads_an_sgml_ofx_statement_without_closing_element_tags()
    {
        var statement = BankStatementParser.Parse("""
            OFXHEADER:100
            DATA:OFXSGML
            VERSION:102

            <OFX>
            <BANKMSGSRSV1><STMTTRNRS><STMTRS>
            <BANKTRANLIST>
            <STMTTRN>
            <TRNTYPE>DEBIT
            <DTPOSTED>20261015120000[+10:AEST]
            <TRNAMT>-45.20
            <FITID>2026101501
            <NAME>COLES 0123
            <MEMO>Card purchase
            </STMTTRN>
            <STMTTRN>
            <TRNTYPE>CREDIT
            <DTPOSTED>20261016
            <TRNAMT>1000.00
            <FITID>2026101602
            <NAME>Dividend BHP &amp; Co
            <MEMO>Dividend BHP &amp; Co
            </STMTTRN>
            </BANKTRANLIST>
            </STMTRS></STMTTRNRS></BANKMSGSRSV1>
            </OFX>
            """, "statement.ofx");

        Assert.Null(statement.FileError);
        Assert.Equal("OFX", statement.Format);
        Assert.Equal(2, statement.ValidCount);
        Assert.Equal(new BankTransaction(new DateOnly(2026, 10, 15), "COLES 0123 Card purchase", -45.20m), statement.Lines[0].Transaction);
        // The same name and memo aren't repeated, and entities are decoded
        Assert.Equal("Dividend BHP & Co", statement.Lines[1].Transaction!.Description);
    }

    [Fact]
    public void Reads_an_xml_ofx_statement()
    {
        var statement = BankStatementParser.Parse("""
            <?xml version="1.0" encoding="UTF-8"?>
            <?OFX OFXHEADER="200" VERSION="220"?>
            <OFX><BANKMSGSRSV1><STMTTRNRS><STMTRS><BANKTRANLIST>
            <STMTTRN><TRNTYPE>INT</TRNTYPE><DTPOSTED>20260630</DTPOSTED><TRNAMT>3.21</TRNAMT><MEMO>Interest</MEMO></STMTTRN>
            </BANKTRANLIST></STMTRS></STMTTRNRS></BANKMSGSRSV1></OFX>
            """);

        var line = Assert.Single(statement.Lines);
        Assert.Equal(new BankTransaction(new DateOnly(2026, 6, 30), "Interest", 3.21m), line.Transaction);
    }

    [Fact]
    public void A_line_without_a_description_says_deposit_or_withdrawal()
    {
        var statement = BankStatementParser.ParseQif("!Type:Bank\nD01/01/2026\nT-3.00\n^");
        Assert.Equal("Withdrawal", statement.Lines[0].Transaction!.Description);
    }

    [Fact]
    public void A_file_with_no_transactions_or_of_another_kind_is_refused()
    {
        Assert.NotNull(BankStatementParser.Parse("!Type:Bank\n", "x.qif").FileError);
        Assert.NotNull(BankStatementParser.Parse("Date,Amount\n1/1/2026,5", "x.csv").FileError);
        Assert.NotNull(BankStatementParser.Parse("<OFX></OFX>", "x.ofx").FileError);
    }

    [Theory]
    [InlineData(0, "a.qif", true)]
    [InlineData(100, "a.QIF", false)]
    [InlineData(100, "a.ofx", false)]
    [InlineData(100, "a.qfx", false)]
    [InlineData(100, "a.csv", true)]
    [InlineData(3 * 1024 * 1024, "a.ofx", true)]
    public void Checks_the_upload(long length, string name, bool refused) =>
        Assert.Equal(refused, BankStatementParser.CheckFile(length, name) is not null);

    [Fact]
    public void Decodes_utf8_and_falls_back_to_latin1()
    {
        Assert.Equal("Café", BankStatementParser.Decode(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("Café")).ToArray()));
        Assert.Equal("Café", BankStatementParser.Decode(Encoding.Latin1.GetBytes("Café")));
    }

    [Fact]
    public void Flags_each_already_imported_transaction_once()
    {
        var statement = BankStatementParser.ParseQif("""
            !Type:Bank
            D01/07/2026
            T-10.00
            PFee
            ^
            D01/07/2026
            T-10.00
            PFee
            ^
            D02/07/2026
            T-10.00
            PFee
            ^
            """);

        var flagged = BankStatementParser.FlagDuplicates(statement, [(new DateOnly(2026, 7, 1), -10m, "  fee ")]);

        Assert.Equal([true, false, false], flagged.Lines.Select(l => l.Duplicate));
        Assert.Equal(1, flagged.DuplicateCount);
    }
}
