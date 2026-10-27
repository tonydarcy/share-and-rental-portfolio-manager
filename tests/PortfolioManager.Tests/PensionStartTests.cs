using PortfolioManager.Core.TaxWork;

namespace PortfolioManager.Tests;

public class PensionStartTests
{
    private static readonly DateOnly YearStart = new(2025, 7, 1), YearEnd = new(2026, 6, 30);

    [Fact]
    public void A_pension_started_during_the_year_has_its_minimum_pro_rated_by_days()
    {
        // 5% for age 66, times 181 of 365 days, to the nearest $10.
        Assert.Equal(12_400m, MinimumPension.ProRata(500_000, 66, new DateOnly(2026, 1, 1), YearStart, YearEnd));
        Assert.Equal(25_000m, MinimumPension.ProRata(500_000, 66, YearStart, YearStart, YearEnd));
    }

    [Fact]
    public void A_pension_started_in_june_has_no_minimum_that_year()
    {
        Assert.Equal(0m, MinimumPension.ProRata(500_000, 66, new DateOnly(2026, 6, 1), YearStart, YearEnd));
    }

    [Fact]
    public void The_account_uses_the_start_balance_and_the_age_on_the_start_day()
    {
        var member = new SmsfMember(1, "Pat", new DateOnly(1960, 3, 1));   // 65 on 1 July 2025, 65 on 1 Feb 2026
        var account = new SmsfMemberAccount(1, 1, 2026, SmsfPhases.Retirement, 0, 0, 0, 400_000, 0, 0, 0,
            PensionStart: new DateOnly(2026, 2, 1), StartBalance: 400_000);
        Assert.True(account.StartedInYear(YearStart));
        // 400,000 x 5% x 150 / 365 = 8,219 -> 8,220
        Assert.Equal(8_220m, MinimumPension.ForAccount(account, member, YearStart));

        var fromJuly = account with { PensionStart = null, StartBalance = null, Opening = 400_000 };
        Assert.Equal(20_000m, MinimumPension.ForAccount(fromJuly, member, YearStart));
    }
}
