using PortfolioManager.Core.Rentals;
using PortfolioManager.Core.TaxWork;

namespace PortfolioManager.Tests;

public class PropertyRecordsTests
{
    private static Tenancy Lease(DateOnly start, decimal rent = 500, string frequency = RentFrequencies.Week, DateOnly? leaseEnd = null, DateOnly? vacated = null,
        decimal? bond = null, string? lodgement = null, DateOnly? review = null) =>
        new(1, 1, "Sam Lee", null, null, start, leaseEnd, rent, frequency, bond, lodgement, review, vacated, null);

    private static RentPayment Paid(DateOnly date, decimal amount) => new(0, 1, date, amount, null);

    [Fact]
    public void Rent_is_due_in_advance_each_period()
    {
        var t = Lease(new DateOnly(2025, 7, 1));
        Assert.Equal(3, RentLedger.Schedule(t, new DateOnly(2025, 7, 15)).Count);
        var monthly = Lease(new DateOnly(2025, 1, 31), 2_000, RentFrequencies.Month);
        Assert.Equal(new DateOnly(2025, 2, 28), RentLedger.Schedule(monthly, new DateOnly(2025, 3, 1))[1].Date);
    }

    [Fact]
    public void Position_shows_arrears_from_the_oldest_unpaid_rent()
    {
        var t = Lease(new DateOnly(2025, 7, 1));
        var p = RentLedger.Position(t, [Paid(new DateOnly(2025, 7, 1), 500)], new DateOnly(2025, 7, 15));
        Assert.Equal(1_500m, p.Due);
        Assert.Equal(1_000m, p.Arrears);
        Assert.Equal(new DateOnly(2025, 7, 7), p.PaidTo);
        Assert.Equal(new DateOnly(2025, 7, 8), p.OldestUnpaid);
        Assert.Equal(7, p.DaysBehind(new DateOnly(2025, 7, 15)));
    }

    [Fact]
    public void Rent_paid_ahead_is_a_credit()
    {
        var t = Lease(new DateOnly(2025, 7, 1));
        var p = RentLedger.Position(t, [Paid(new DateOnly(2025, 7, 1), 2_000)], new DateOnly(2025, 7, 2));
        Assert.Equal(1_500m, p.Credit);
        Assert.Equal(new DateOnly(2025, 7, 28), p.PaidTo);
        Assert.Null(p.OldestUnpaid);
    }

    [Fact]
    public void Rent_stops_when_the_tenants_leave()
    {
        var t = Lease(new DateOnly(2025, 7, 1), vacated: new DateOnly(2025, 7, 10));
        Assert.Equal(2, RentLedger.Schedule(t, new DateOnly(2025, 12, 31)).Count);
        Assert.False(t.Current(new DateOnly(2025, 7, 11)));
    }

    [Fact]
    public void Reminders_for_leases_reviews_bonds_arrears_and_checks()
    {
        var today = new DateOnly(2025, 9, 1);
        var house = new RentalProperty(1, "1 Test St", null, 100, true, null);
        var t = Lease(new DateOnly(2025, 7, 1), leaseEnd: new DateOnly(2025, 10, 15), bond: 2_000, review: new DateOnly(2025, 9, 20));
        var checks = new[]
        {
            new ComplianceItem(5, 1, "Smoke alarm check", new DateOnly(2024, 9, 10), 12, null, null),
            new ComplianceItem(6, 1, "Gas safety check", new DateOnly(2025, 1, 1), 24, null, null),
        };
        var r = PropertyReminders.Due([house], [t], [Paid(new DateOnly(2025, 7, 1), 500)], checks, today);
        Assert.Contains(r, x => x.Key == "lease:1:20251015");
        Assert.Contains(r, x => x.Key == "review:1:20250920");
        Assert.Contains(r, x => x.Key == "bond:1");
        Assert.Contains(r, x => x.Key.StartsWith("arrears:1:") && x.Urgent);
        Assert.Contains(r, x => x.Key == "check:5:20250910");
        Assert.DoesNotContain(r, x => x.Key.StartsWith("check:6:"));
    }

    [Fact]
    public void Next_due_is_the_set_date_or_last_done_plus_months()
    {
        Assert.Equal(new DateOnly(2026, 3, 1), new ComplianceItem(1, 1, "x", new DateOnly(2025, 3, 1), 12, null, null).NextDue);
        Assert.Equal(new DateOnly(2025, 5, 1), new ComplianceItem(1, 1, "x", new DateOnly(2025, 3, 1), 12, new DateOnly(2025, 5, 1), null).NextDue);
        Assert.Null(new ComplianceItem(1, 1, "x", null, 12, null, null).NextDue);
    }

    [Fact]
    public void Presets_add_state_checks_to_the_usual_ones()
    {
        Assert.Equal(3, CompliancePresets.For(null).Count);
        Assert.Contains(CompliancePresets.For("VIC"), p => p.Kind == "Gas safety check" && p.Months == 24);
        Assert.DoesNotContain(CompliancePresets.For("NSW"), p => p.Kind == "Gas safety check");
        Assert.All(CompliancePresets.States, s => Assert.Contains("Smoke alarm check", CompliancePresets.For(s).Select(p => p.Kind)));
    }

    [Fact]
    public void Capital_jobs_and_tenant_bill_categories()
    {
        Assert.True(new RentalJob(1, 1, new DateOnly(2025, 1, 1), "New kitchen", null, 20_000, JobTreatments.Improvement, null).IsCapital);
        Assert.False(new RentalJob(1, 1, new DateOnly(2025, 1, 1), "Fix tap", null, 200, JobTreatments.Repair, null).IsCapital);
        Assert.All(TenantBillCategories.Keys, k => Assert.True(RentalCategories.IsExpense(k)));
    }
}
