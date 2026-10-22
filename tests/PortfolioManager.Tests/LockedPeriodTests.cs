using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Regions;

namespace PortfolioManager.Tests;

public class LockedPeriodTests
{
    private static readonly YearStart July = new(7, 1);
    private static readonly YearStart January = new(1, 1);

    [Fact]
    public void Everything_on_or_before_the_lock_date_is_locked()
    {
        var lockTo = new LockedPeriod(new DateOnly(2025, 6, 30));

        Assert.True(lockTo.IsLocked(new DateOnly(2025, 6, 30)));
        Assert.True(lockTo.IsLocked(new DateOnly(2019, 1, 1)));
        Assert.False(lockTo.IsLocked(new DateOnly(2025, 7, 1)));
        Assert.False(LockedPeriod.None.IsLocked(new DateOnly(2000, 1, 1)));
    }

    [Fact]
    public void A_year_is_locked_when_its_last_day_is()
    {
        var lockTo = new LockedPeriod(new DateOnly(2025, 6, 30));

        Assert.True(lockTo.IsYearLocked(2025, July));
        Assert.True(lockTo.IsYearLocked(2024, July));
        Assert.False(lockTo.IsYearLocked(2026, July));
        Assert.False(lockTo.IsYearLocked(2025, January));
    }

    [Fact]
    public void Unlocking_a_year_moves_the_lock_back_to_the_year_before()
    {
        Assert.Equal(new DateOnly(2024, 6, 30), LockedPeriod.UnlockTo(2025, July));
        Assert.Equal(new DateOnly(2024, 12, 31), LockedPeriod.UnlockTo(2025, January));
    }

    [Fact]
    public void The_lock_row_maps_and_no_row_means_unlocked()
    {
        var row = new Dictionary<string, object?>
        {
            ["dtmLockedTo"] = new DateTime(2025, 6, 30),
            ["dtmChangedDate"] = new DateTime(2025, 10, 1, 9, 30, 0),
            ["strChangedBy"] = "tony@example.com",
        };

        var mapped = LockedPeriodService.Map(row);
        Assert.Equal(new DateOnly(2025, 6, 30), mapped.LockedTo);
        Assert.Equal("tony@example.com", mapped.ChangedBy);
        Assert.Same(LockedPeriod.None, LockedPeriodService.Map(null));
    }

    [Fact]
    public void Other_errors_are_not_mistaken_for_a_lock()
    {
        Assert.False(LockedPeriodService.IsLockError(new InvalidOperationException("x", new Exception("y"))));
        Assert.False(LockedPeriodService.IsLockError(null));
    }
}
