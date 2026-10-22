using System.Globalization;
using PortfolioManager.Core.Bills;
using PortfolioManager.Core.Reading;
using TextReader = PortfolioManager.Core.Reading.TextReader;

namespace PortfolioManager.Tests;

public class DocumentReadingTests
{
    private static readonly CultureInfo Au = CultureInfo.GetCultureInfo("en-AU");

    [Fact]
    public void Reads_a_tax_invoice()
    {
        var r = TextReader.Read("""
            Officeworks Pty Ltd
            ABN 12 345 678 901
            Tax Invoice
            Invoice No: INV-1234
            Date: 15/10/2026
            Paper $50.00
            Subtotal $100.00
            GST $10.00
            Total $110.00
            """, Au);
        Assert.Equal(ReadKinds.Bill, r.Kind);
        Assert.Equal("Officeworks Pty Ltd", r.Payee);
        Assert.Equal("12345678901", r.TaxNumber);
        Assert.Equal("INV-1234", r.Reference);
        Assert.Equal(new DateOnly(2026, 10, 15), r.Date);
        Assert.Equal(110m, r.Total);
        Assert.Equal(10m, r.Tax);
    }

    [Fact]
    public void Reads_a_contract_note()
    {
        var r = TextReader.Read("""
            CommSec
            Bought 100 BHP shares @ $45.50
            Units: 100
            Brokerage $19.95
            Total $4,569.95
            """, Au);
        Assert.Equal(ReadKinds.Trade, r.Kind);
        Assert.Equal(("BHP", "Buy", 100m, 45.50m, 19.95m), (r.TradeCode, r.TradeSide, r.Units, r.Price, r.Brokerage));
        Assert.Equal(4_569.95m, r.Total);
    }

    [Fact]
    public void Dates_are_day_first_unless_the_format_is_month_first()
    {
        Assert.Equal(new DateOnly(2026, 2, 3), TextReader.ParseDate("3/2/2026", Au));
        Assert.Equal(new DateOnly(2026, 3, 2), TextReader.ParseDate("3/2/2026", CultureInfo.GetCultureInfo("en-US")));
        Assert.Equal(new DateOnly(2026, 10, 15), TextReader.ParseDate("15 Oct 2026", Au));
    }

    [Fact]
    public void Claude_answer_is_parsed_and_bad_values_become_blanks()
    {
        var r = DocumentReadingService.FromJson("""
            {"kind":"Trade","payee":"CommSec","date":"2026-10-15","reference":null,"abn":"12 345 678 901","description":"","total":4569.95,
             "tax":null,"currency":"usd","dueDate":"15/10/2026","tradeCode":"bhp","tradeSide":"sell","units":100,"price":45.5,"brokerage":"x"}
            """)!;
        Assert.Equal(ReadKinds.Trade, r.Kind);
        Assert.Equal("12345678901", r.TaxNumber);
        Assert.Null(r.Description);
        Assert.Equal("USD", r.Currency);
        Assert.Null(r.DueDate);
        Assert.Equal(("BHP", "Sell"), (r.TradeCode, r.TradeSide));
        Assert.Null(r.Brokerage);
        Assert.Equal("Claude", r.Source);
        Assert.Null(DocumentReadingService.FromJson("not json"));
    }

    private static Supplier Supplier(int id, string name, string? abn = null) =>
        new(id, name, null, null, null, null, abn, null, null, null, null, true);

    [Fact]
    public void Supplier_is_matched_by_ABN_then_name()
    {
        Supplier[] suppliers = [Supplier(1, "Officeworks"), Supplier(2, "Telstra", "33 051 775 556")];
        var read = new ReadResult(ReadKinds.Bill, "Officeworks Pty Ltd", null, null, "33051775556", null, 10, null, null);
        Assert.Equal(2, ReadingDrafts.MatchSupplier(suppliers, read)!.SupplierId);
        Assert.Equal(1, ReadingDrafts.MatchSupplier(suppliers, read with { TaxNumber = null })!.SupplierId);
        Assert.Null(ReadingDrafts.MatchSupplier(suppliers, read with { TaxNumber = null, Payee = "Bunnings" }));
    }

    [Fact]
    public void Draft_bill_keeps_a_foreign_currency_and_a_sensible_due_date()
    {
        var read = new ReadResult(ReadKinds.Bill, "Acme", new DateOnly(2026, 10, 15), "INV-9", null, null, 220, 20, "AUD", new DateOnly(2026, 10, 1));
        var bill = ReadingDrafts.Bill(read, 4, 30, 50, 5, new DateTime(2026, 10, 21), "AUD");
        Assert.Null(bill.Currency);
        Assert.Equal(new DateTime(2026, 11, 14), bill.DueDate);
        Assert.Equal("INV-9", bill.Number);
        Assert.Equal(220m, Assert.Single(bill.Lines).UnitPrice);
        Assert.Equal("USD", ReadingDrafts.Bill(read with { Currency = "USD" }, 4, 30, 50, 5, new DateTime(2026, 10, 21), "AUD").Currency);
    }

    [Fact]
    public void No_tax_read_means_no_tax_code()
    {
        var read = new ReadResult(ReadKinds.Receipt, "Cafe", null, null, null, null, 12, null, null);
        Assert.Null(ReadingDrafts.TaxCodeFor(read, [], 5));
        Assert.Equal(5, ReadingDrafts.TaxCodeFor(read with { Tax = 1.09m }, [], 5));
    }
}
