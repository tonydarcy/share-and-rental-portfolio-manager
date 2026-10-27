using PortfolioManager.Core.Invoicing;

namespace PortfolioManager.Tests;

public class CreditRefundSplitDepositTests
{
    private static InvoiceSummary Inv(int id, DateOnly due, decimal total, decimal paid = 0) =>
        new(id, $"INV-{id}", 1, "Customer", due.AddDays(-14), due, null, InvoiceStatus.Approved, total, 0, total, paid);

    [Fact]
    public void A_deposit_fills_the_oldest_invoices_first()
    {
        var open = new[] { Inv(2, new DateOnly(2026, 2, 1), 800), Inv(1, new DateOnly(2026, 1, 1), 1000), Inv(3, new DateOnly(2026, 3, 1), 300) };
        var split = DepositSplit.Suggest(1500, open);
        Assert.Equal([(1, 1000m), (2, 500m)], split.Select(s => (s.InvoiceId, s.Amount)));
    }

    [Fact]
    public void A_split_must_add_up_to_the_deposit_and_not_overpay_an_invoice()
    {
        var owing = new Dictionary<int, decimal> { [1] = 1000, [2] = 800 };
        Assert.Null(DepositSplit.Validate(1500, [new(1, 1000), new(2, 500)], owing));
        Assert.Contains("add up to 1,400.00", DepositSplit.Validate(1500, [new(1, 1000), new(2, 400)], owing));
        Assert.Contains("more than is owing", DepositSplit.Validate(1500, [new(1, 1200), new(2, 300)], owing));
        Assert.Contains("at least one", DepositSplit.Validate(1500, [new(1, 0)], owing));
        Assert.Equal("<p><a i=\"1\" v=\"1000\" /><a i=\"2\" v=\"500.5\" /></p>", DepositSplit.Xml([new(1, 1000), new(2, 500.5m), new(3, 0)]));
    }

    [Fact]
    public void A_refund_of_a_credit_note_shows_on_the_customer_statement()
    {
        var customer = new Customer(1, "Customer", null, null, null, null, null, true);
        var credit = new InvoiceDetail(5, "CN-5", 1, new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 1), null, null, false, InvoiceStatus.Approved, 100, 0, 100,
            null, customer, [], [new InvoicePayment(9, new DateOnly(2026, 4, 10), 100, 1, "Bank", "Refund (EFT)", 7)]);
        var moves = Receivables.Movements([credit], new Dictionary<int, InvoiceKind> { [5] = new(InvoiceKinds.CreditNote, null) });
        Assert.Equal([-100m, 100m], moves.Select(m => m.Amount));
        Assert.Equal("Refund", moves[1].Document);
    }
}
