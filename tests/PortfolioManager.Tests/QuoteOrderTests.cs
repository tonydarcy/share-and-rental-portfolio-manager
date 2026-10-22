using PortfolioManager.Core.Invoicing;
using PortfolioManager.Core.Orders;

namespace PortfolioManager.Tests;

public class QuoteOrderTests
{
    private static QuoteOrder Make(string kind, string status, DateOnly? expiry = null) =>
        new(7, kind, kind == QuoteOrderKinds.Quote ? "Q-0007" : "PO-0007", 3, "Acme", new DateOnly(2026, 10, 1), expiry, "Fit-out", null, "Thanks",
            true, status, 100, 10, 110, "USD", 0.65m, null,
            [new InvoiceLine(1, "Desk", 2, 55, 40, 5, "GST", 100, 10, 110, ItemId: 9, Tracking1Id: 11)]);

    [Fact]
    public void A_quote_becomes_a_draft_invoice_with_its_lines_and_rate()
    {
        var input = Make(QuoteOrderKinds.Quote, QuoteOrderStatus.Accepted).ToInvoice(new DateTime(2026, 10, 21), 14);
        Assert.Equal(3, input.CustomerId);
        Assert.Equal(new DateTime(2026, 11, 4), input.DueDate);
        Assert.Equal("Q-0007", input.Reference);
        Assert.Equal("USD", input.Currency);
        Assert.Equal(0.65m, input.ExchangeRate);
        var line = Assert.Single(input.Lines);
        Assert.Equal((2m, 55m, 40, 5, 9, 11), (line.Quantity, line.UnitPrice, line.AccountId, line.TaxCodeId, line.ItemId, line.Tracking1Id));
    }

    [Fact]
    public void A_purchase_order_becomes_a_bill_linked_back_to_it()
    {
        var input = Make(QuoteOrderKinds.PurchaseOrder, QuoteOrderStatus.Sent).ToBill(new DateTime(2026, 10, 20), 30, " INV-55 ");
        Assert.Equal(3, input.SupplierId);
        Assert.Equal("INV-55", input.Number);
        Assert.Equal("PO-0007", input.Reference);
        Assert.Equal(7, input.PurchaseOrderId);
        Assert.Equal(new DateTime(2026, 11, 19), input.DueDate);
    }

    [Theory]
    [InlineData(QuoteOrderStatus.Draft, true)]
    [InlineData(QuoteOrderStatus.Accepted, true)]
    [InlineData(QuoteOrderStatus.Declined, false)]
    [InlineData(QuoteOrderStatus.Invoiced, false)]
    public void Only_open_quotes_can_be_converted(string status, bool can) =>
        Assert.Equal(can, Make(QuoteOrderKinds.Quote, status).CanConvert);

    [Fact]
    public void A_sent_quote_past_its_expiry_is_expired()
    {
        var today = new DateOnly(2026, 10, 21);
        Assert.True(Make(QuoteOrderKinds.Quote, QuoteOrderStatus.Sent, new DateOnly(2026, 10, 20)).IsExpired(today));
        Assert.False(Make(QuoteOrderKinds.Quote, QuoteOrderStatus.Accepted, new DateOnly(2026, 10, 20)).IsExpired(today));
        Assert.False(Make(QuoteOrderKinds.PurchaseOrder, QuoteOrderStatus.Sent, new DateOnly(2026, 10, 20)).IsExpired(today));
    }
}
