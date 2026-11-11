namespace PortfolioManager.Mobile.Client;

/// <summary>The periods the app's Payouts tab offers. The financial year runs 1 July to 30 June (the site's default).</summary>
public static class ReportPeriod
{
    public static readonly IReadOnlyList<string> Choices = ["Last 12 months", "This financial year", "Last financial year"];

    /// <summary>The first and last day of a choice from <see cref="Choices"/>.</summary>
    public static (DateOnly From, DateOnly To) Range(int choice, DateOnly today)
    {
        var yearStart = new DateOnly(today.Month >= 7 ? today.Year : today.Year - 1, 7, 1);
        return choice switch
        {
            1 => (yearStart, yearStart.AddYears(1).AddDays(-1)),
            2 => (yearStart.AddYears(-1), yearStart.AddDays(-1)),
            _ => (today.AddYears(-1).AddDays(1), today),
        };
    }

    /// <summary>The calendar year a financial year ends in: this one (0) or one before (1), the number the site's reports take.</summary>
    public static int YearEnding(DateOnly today, int yearsBack = 0) => (today.Month >= 7 ? today.Year + 1 : today.Year) - yearsBack;
}
