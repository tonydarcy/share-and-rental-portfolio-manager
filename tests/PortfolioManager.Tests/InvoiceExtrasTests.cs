using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Invoicing;

namespace PortfolioManager.Tests;

public class InvoiceExtrasTests
{
    private static readonly DateOnly AsAt = new(2026, 6, 30);

    private static InvoiceSummary Inv(int id, int customer, DateOnly due, decimal total, decimal paid = 0, string status = InvoiceStatus.Approved) =>
        new(id, $"INV-{id}", customer, $"Customer {customer}", due.AddDays(-14), due, null, status, total, 0, total, paid);

    [Fact]
    public void Aged_receivables_go_in_buckets_by_days_overdue()
    {
        var invoices = new[]
        {
            Inv(1, 1, AsAt.AddDays(5), 100), Inv(2, 1, AsAt.AddDays(-10), 200), Inv(3, 1, AsAt.AddDays(-45), 300),
            Inv(4, 1, AsAt.AddDays(-75), 400), Inv(5, 1, AsAt.AddDays(-120), 500, paid: 100), Inv(6, 1, AsAt.AddDays(-10), 999, status: InvoiceStatus.Draft),
            Inv(7, 2, AsAt.AddDays(-10), 50, paid: 50),
        };
        var aged = Receivables.Aged(invoices, new Dictionary<int, InvoiceKind>(), AsAt);
        var a = Assert.Single(aged);
        Assert.Equal((100m, 200m, 300m, 400m, 400m), (a.Current, a.Days30, a.Days60, a.Days90, a.Older));
        Assert.Equal(1400m, a.Total);
    }

    [Fact]
    public void Unapplied_credit_notes_reduce_the_current_column()
    {
        var invoices = new[] { Inv(1, 1, AsAt.AddDays(-10), 200), Inv(2, 1, AsAt, 50) };
        var kinds = new Dictionary<int, InvoiceKind> { [2] = new(InvoiceKinds.CreditNote, 1) };
        var a = Assert.Single(Receivables.Aged(invoices, kinds, AsAt));
        Assert.Equal(-50m, a.Current);
        Assert.Equal(150m, a.Total);
    }

    private static InvoiceDetail Detail(int id, DateOnly date, decimal total, params InvoicePayment[] payments) =>
        new(id, $"INV-{id}", 1, date, date.AddDays(14), null, null, false, InvoiceStatus.Approved, total, 0, total, null,
            new Customer(1, "Customer", null, null, null, null, null, true), [], payments);

    [Fact]
    public void Statement_has_an_opening_balance_and_a_running_balance()
    {
        var docs = new[]
        {
            Detail(1, new DateOnly(2026, 3, 1), 500, new InvoicePayment(1, new DateOnly(2026, 4, 10), 300, 1, "Bank", null, null)),
            Detail(2, new DateOnly(2026, 4, 20), 200, new InvoicePayment(2, new DateOnly(2026, 4, 25), 50, 1, "Bank", "Credit note CN-3", null)),
            Detail(3, new DateOnly(2026, 4, 25), 50),
        };
        var kinds = new Dictionary<int, InvoiceKind> { [3] = new(InvoiceKinds.CreditNote, 2) };
        var moves = Receivables.Movements(docs, kinds);
        Assert.Equal(4, moves.Count);
        var (opening, lines) = Receivables.Statement(moves, new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 30));
        Assert.Equal(500m, opening);
        Assert.Equal(3, lines.Count);
        Assert.Equal(300m, lines[0].Paid);
        Assert.Equal(350m, lines[^1].Balance);
    }

    [Fact]
    public void Repeating_invoice_dates_and_lines()
    {
        var lines = new[] { new InvoiceLineInput { Description = "Monthly fee", Quantity = 1, UnitPrice = 110, AccountId = 40, TaxCodeId = 1 } };
        var r = new RepeatingInvoice(1, 5, "Customer", "Fee", RepeatFrequencies.Monthly, new DateOnly(2026, 1, 15), null, 2, 14, true, true, "Ref", null,
            RepeatingInvoice.ParseLines(RepeatingInvoice.LinesXml(lines)), true, null);
        Assert.Equal([new DateOnly(2026, 3, 15), new DateOnly(2026, 4, 15)], r.DueDates(new DateOnly(2026, 4, 20)));
        var input = r.ToInput(new DateOnly(2026, 3, 15));
        Assert.Equal(new DateTime(2026, 3, 29), input.DueDate);
        Assert.Equal(110m, input.Lines[0].UnitPrice);
        Assert.Equal(1, input.Lines[0].TaxCodeId);
    }
}
