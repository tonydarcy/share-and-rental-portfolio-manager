using PortfolioManager.Core.Regions;

namespace PortfolioManager.Core.Tax;

/// <summary>
/// Financial years, identified by the year they end in. They start on the portfolio's year start
/// (RegionSettings.Current, 1 July in Australia) unless one is passed in.
/// </summary>
public static class FinancialYear
{
    private static YearStart Of(YearStart? start) => start ?? RegionSettings.Current.YearStart;

    /// <summary>The year a financial year ends in, e.g. 2027 for 1-Jul-2026 to 30-Jun-2027.</summary>
    public static int EndYearFor(DateOnly date, YearStart? start = null)
    {
        var s = Of(start);
        if (s.IsCalendarYear)
            return date.Year;
        return date >= new DateOnly(date.Year, s.Month, s.Day) ? date.Year + 1 : date.Year;
    }

    public static DateOnly StartDate(int endYear, YearStart? start = null)
    {
        var s = Of(start);
        return s.IsCalendarYear ? new(endYear, 1, 1) : new(endYear - 1, s.Month, s.Day);
    }

    public static DateOnly EndDate(int endYear, YearStart? start = null) => StartDate(endYear + 1, start).AddDays(-1);

    /// <summary>Label shown in the dropdowns, e.g. "2026-27", or "2026" for a calendar year.</summary>
    public static string Label(int endYear, YearStart? start = null) =>
        Of(start).IsCalendarYear ? $"{endYear}" : $"{endYear - 1}-{endYear % 100:00}";

    /// <summary>The last completed financial year, the one you'd be lodging a return for.</summary>
    public static int LastCompleted(DateOnly today, YearStart? start = null) => EndYearFor(today, start) - 1;

    /// <summary>The current financial year and the ones before it, newest first.</summary>
    public static IReadOnlyList<int> Recent(DateOnly today, int count = 11, YearStart? start = null)
    {
        var current = EndYearFor(today, start);
        return Enumerable.Range(0, count).Select(i => current - i).ToList();
    }

    /// <summary>
    /// Works out the dates a report covers from its query string. A financial year wins over
    /// dates (the date boxes still hold the old year's dates when the dropdown changes);
    /// otherwise both dates are used; otherwise the last completed year.
    /// </summary>
    public static ReportPeriod Resolve(int? fy, DateOnly? from, DateOnly? to, DateOnly today, YearStart? start = null)
    {
        if (fy is >= 1901 and <= 9999)
            return ForYear(fy.Value, null, start);

        if (from is not null && to is not null)
        {
            if (from > to)
                return ForYear(LastCompleted(today, start), "The From date is after the To date, so the last financial year is shown instead.", start);
            return new ReportPeriod(null, from.Value, to.Value, null);
        }

        return ForYear(LastCompleted(today, start), null, start);
    }

    private static ReportPeriod ForYear(int endYear, string? warning, YearStart? start) =>
        new(endYear, StartDate(endYear, start), EndDate(endYear, start), warning, Label(endYear, start));
}

/// <summary>The dates a report covers. FinancialYear is null for custom dates.</summary>
public sealed record ReportPeriod(int? FinancialYear, DateOnly From, DateOnly To, string? Warning, string? YearLabel = null)
{
    public string Description => FinancialYear is { } y
        ? $"Financial year {YearLabel ?? Tax.FinancialYear.Label(y)} ({From:d MMM yyyy} to {To:d MMM yyyy})"
        : $"{From:d MMM yyyy} to {To:d MMM yyyy}";
}
