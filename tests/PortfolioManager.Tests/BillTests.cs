using PortfolioManager.Core.Bills;
using PortfolioManager.Core.Invoicing;

namespace PortfolioManager.Tests;

public class BillTests
{
    private static readonly DateOnly AsAt = new(2026, 10, 21);

    private static BillSummary Bill(int id, DateOnly due, decimal total, decimal paid = 0, string kind = BillKinds.Bill,
        string status = InvoiceStatus.Approved, string? currency = null, decimal? home = null, int supplier = 1) =>
        new(id, kind, $"B{id}", supplier, supplier == 1 ? "Acme" : "Bolt", due.AddDays(-30), due, null, status, total, paid, currency, home);

    [Fact]
    public void Aged_payables_put_each_bill_in_its_overdue_bucket()
    {
        var aged = Payables.Aged([
            Bill(1, new DateOnly(2026, 10, 30), 100),
            Bill(2, new DateOnly(2026, 9, 1), 250, paid: 50),
            Bill(3, new DateOnly(2026, 5, 1), 70),
        ], AsAt);
        var a = Assert.Single(aged);
        Assert.Equal(100m, a.Current);
        Assert.Equal(200m, a.Days60);
        Assert.Equal(70m, a.Older);
        Assert.Equal(370m, a.Total);
    }

    [Fact]
    public void Aged_payables_take_off_credits_and_leave_out_drafts_and_paid_bills()
    {
        var aged = Payables.Aged([
            Bill(1, new DateOnly(2026, 10, 30), 100),
            Bill(2, new DateOnly(2026, 10, 30), 30, kind: BillKinds.SupplierCredit),
            Bill(3, new DateOnly(2026, 10, 30), 500, status: InvoiceStatus.Draft),
            Bill(4, new DateOnly(2026, 10, 30), 80, paid: 80, supplier: 2),
        ], AsAt);
        Assert.Equal(70m, Assert.Single(aged).Current);
    }

    [Fact]
    public void A_foreign_bill_is_aged_in_the_portfolio_currency_at_its_rate()
    {
        var bill = Bill(1, new DateOnly(2026, 10, 30), 100, paid: 40, currency: "USD", home: 150);
        Assert.Equal(90m, bill.HomeBalance);
        Assert.Equal(90m, Assert.Single(Payables.Aged([bill], AsAt)).Current);
    }
}
