using PortfolioManager.Core.SalesTax;

namespace PortfolioManager.Tests;

public class ActivityStatementTests
{
    private static SalesTaxReport Gst(params (string Box, decimal Amount)[] boxes) =>
        new("GST", new DateOnly(2026, 7, 1), new DateOnly(2026, 9, 30), [], [.. boxes.Select(b => new ReturnBox(b.Box, b.Amount, []))], [],
            new Dictionary<int, TaxCode>());

    private static readonly SalesTaxReport Quarter = Gst(("G1", 1100), ("1A", 100), ("G11", 550.75m), ("1B", 50.75m));

    [Fact]
    public void Instalment_income_times_the_rate_gives_the_instalment_and_the_totals()
    {
        var bas = ActivityStatementBuilder.Build(Quarter, new BasSettings(false, PaygMethods.Rate, null, 2),
            new Dictionary<string, decimal> { ["W1"] = 5000, ["W2"] = 1000 }, 10_000.4m);
        Assert.Equal(10_000m, bas.Amount("T1"));
        Assert.Equal(200m, bas.Amount("T11"));
        Assert.Equal(1_000m, bas.Amount("W5"));
        Assert.Equal(550m, bas.Amount("G11"));       // cents left off
        Assert.Equal(1_300m, bas.Amount("8A"));      // 100 + 1,000 + 200
        Assert.Equal(50m, bas.Amount("8B"));
        Assert.Equal(1_250m, bas.Amount("9"));
        Assert.Null(bas.Warning);
    }

    [Fact]
    public void A_varied_instalment_wins_and_needs_a_reason()
    {
        var bas = ActivityStatementBuilder.Build(Quarter, new BasSettings(false, PaygMethods.Amount, 900, null),
            new Dictionary<string, decimal> { ["T9"] = 400 }, 0);
        Assert.Equal(400m, bas.Amount("5A"));
        Assert.Null(bas.Amount("T7"));
        Assert.NotNull(bas.Warning);
    }

    [Fact]
    public void The_ato_amount_option_uses_the_instalment_from_settings()
    {
        var bas = ActivityStatementBuilder.Build(Quarter, new BasSettings(false, PaygMethods.Amount, 900, null), new Dictionary<string, decimal>(), 0);
        Assert.Equal(900m, bas.Amount("T7"));
        Assert.Equal(950m, bas.Amount("9"));
    }

    [Fact]
    public void Fuel_tax_credits_and_fbt_go_into_the_summary()
    {
        var bas = ActivityStatementBuilder.Build(Gst(), BasSettings.Default, new Dictionary<string, decimal> { ["F1"] = 300, ["7D"] = 120 }, 0);
        Assert.Equal(300m, bas.Amount("8A"));
        Assert.Equal(120m, bas.Amount("8B"));
        Assert.Equal(180m, bas.Net);
    }

    [Fact]
    public void Cash_basis_swaps_invoices_and_bills_for_their_payments()
    {
        var day = new DateOnly(2026, 8, 1);
        SalesTaxLine L(int j, string source, decimal net) => new(j, day, "x", source, "Sales", 1, net, net / 10);
        var lines = ActivityStatementBuilder.CashBasis([L(1, "Invoice", -1000), L(2, "Bill", 500), L(3, "Journal", -200)], [L(1, "Invoice", -400)]);
        Assert.Equal([(3, -200m), (1, -400m)], lines.Select(l => (l.JournalId, l.Net)));
    }

    [Fact]
    public void Instalment_income_leaves_out_gains_and_franking()
    {
        Assert.Equal(1_500m, ActivityStatementService.InstalmentIncome([("Sales", 1000), ("Interest", 500), ("Capital gains", 2000), ("Franking credits", 300)]));
    }
}
