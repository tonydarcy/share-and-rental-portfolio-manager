using PortfolioManager.Core.Rentals;
using PortfolioManager.Core.TaxWork;

namespace PortfolioManager.Tests;

public class RentalExtrasTests
{
    private static RentalAsset Pooled(int id, DateOnly start, decimal cost, DateOnly? disposed = null, decimal? tv = null) =>
        new(id, 1, RentalAssetKinds.Plant, $"Asset {id}", start, cost, null, RentalAssetKinds.LowValuePool, null, disposed, tv);

    [Fact]
    public void The_pool_is_worked_out_as_a_whole_and_a_sale_comes_off_the_balance()
    {
        var a = Pooled(1, new DateOnly(2023, 9, 1), 800);
        var b = Pooled(2, new DateOnly(2024, 10, 1), 600, new DateOnly(2025, 3, 1), 500);
        var y2024 = RentalLowValuePool.For([a, b], 2024);
        Assert.Equal(150m, y2024.Deduction);
        Assert.Equal(650m, y2024.Closing);
        var y2025 = RentalLowValuePool.For([a, b], 2025);
        Assert.Equal(356.25m, y2025.Deduction);       // 650 x 37.5% + 600 x 18.75%
        Assert.Equal(500m, y2025.TerminationValues);
        Assert.Equal(393.75m, y2025.Closing);
        Assert.Equal(147.66m, RentalLowValuePool.For([a, b], 2026).Deduction);
        Assert.Equal(0m, RentalDepreciation.BalancingAdjustment(b, 2025));
    }

    [Fact]
    public void A_sale_for_more_than_the_pool_balance_is_income()
    {
        var a = Pooled(1, new DateOnly(2023, 9, 1), 800);
        var b = Pooled(2, new DateOnly(2024, 10, 1), 600, new DateOnly(2025, 3, 1), 1_500);
        var y = RentalLowValuePool.For([a, b], 2025);
        Assert.Equal(0m, y.Closing);
        Assert.Equal(606.25m, y.ExcessIncome);
        var line = RentalLowValuePool.Line(y, 1, new DateOnly(2024, 7, 1));
        Assert.NotNull(line);
        Assert.Equal(0, line.Asset.Id);
    }

    private static readonly RentalJob Kitchen = new(5, 1, new DateOnly(2026, 2, 1), "Kitchen renovation", "Bob", 10_000, JobTreatments.Improvement, null);

    [Fact]
    public void A_job_splits_into_depreciating_assets_and_capital_works()
    {
        var (assets, error) = JobSplit.Build(Kitchen,
            [new JobPart("Oven", 1_500, RentalAssetKinds.DiminishingValue, 12), new JobPart("Carpet", 900, RentalAssetKinds.LowValuePool, null)]);
        Assert.Null(error);
        Assert.Equal(3, assets.Count);
        Assert.True(assets[1].IsPooled);
        var building = assets[2];
        Assert.Equal(RentalAssetKinds.Building, building.Kind);
        Assert.Equal(7_600m, building.Cost);
        Assert.Equal(2.5m, building.Rate);
    }

    [Fact]
    public void A_split_is_refused_when_it_doesnt_fit()
    {
        Assert.NotNull(JobSplit.Build(Kitchen, [new JobPart("Dishwasher", 1_000, RentalAssetKinds.LowValuePool, null)]).Error);
        Assert.NotNull(JobSplit.Build(Kitchen, [new JobPart("Oven", 1_500, RentalAssetKinds.DiminishingValue, null)]).Error);
        Assert.NotNull(JobSplit.Build(Kitchen, [new JobPart("Everything", 12_000, RentalAssetKinds.PrimeCost, 10)]).Error);
    }

    private static readonly Tenancy Lease = new(1, 1, "Sam Lee", "sam@example.com", null, new DateOnly(2026, 1, 1), null, 500, RentFrequencies.Week,
        null, null, null, null, null);

    [Fact]
    public void A_reminder_is_only_made_when_rent_is_owing()
    {
        var today = new DateOnly(2026, 3, 1);
        var paidUp = new RentPosition(Lease, 4_000, 4_000, today, today.AddDays(7), null);
        Assert.Null(TenantEmail.Reminder(Lease, paidUp, today, "1 Main St", "Pat <Landlord>", null));

        var behind = paidUp with { Paid = 3_000, OldestUnpaid = new DateOnly(2026, 2, 15) };
        var email = TenantEmail.Reminder(Lease, behind, today, "1 Main St", "Pat <Landlord>", "pat@example.com", "Thanks");
        Assert.NotNull(email);
        Assert.Equal("sam@example.com", email.To);
        Assert.Equal("pat@example.com", email.ReplyTo);
        Assert.Contains("Thanks", email.Text);
        Assert.DoesNotContain("<Landlord>", email.Html);
    }

    [Fact]
    public void A_receipt_names_the_payment_and_what_the_rent_is_paid_to()
    {
        var position = new RentPosition(Lease, 4_000, 4_000, new DateOnly(2026, 3, 7), null, null);
        var email = TenantEmail.Receipt(Lease, new RentPayment(9, 1, new DateOnly(2026, 3, 1), 500, "Bank transfer"), position, "1 Main St", "Pat", null);
        Assert.StartsWith("Rent receipt", email.Subject);
        Assert.Contains("Bank transfer", email.Text);
        Assert.Contains("2026", email.Text);
    }
}
