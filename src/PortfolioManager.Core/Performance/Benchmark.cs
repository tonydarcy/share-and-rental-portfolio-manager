namespace PortfolioManager.Core.Performance;

/// <summary>
/// Something to compare the portfolio with: a market index (tblMarketIndexHistory) or any share or ETF
/// with prices on file, such as ASX:STW for the ASX 200. Key is "INDEX:XJO" or "ASX:STW".
/// </summary>
public sealed record BenchmarkChoice(string Key, string Label);

public sealed record BenchmarkPoint(DateOnly Date, double Value);

/// <summary>The benchmark's price return over the same days as the portfolio (dividends not included).</summary>
public sealed record BenchmarkResult(
    BenchmarkChoice Choice,
    double? Return,
    IReadOnlyList<(DateOnly Date, double Cumulative)> Days,
    IReadOnlyList<PeriodReturn> Months);

public static class Benchmark
{
    public const string IndexPrefix = "INDEX";

    /// <summary>ETFs that track the main Australian indexes, offered when they have prices on file.</summary>
    public static readonly IReadOnlyList<BenchmarkChoice> SuggestedEtfs =
    [
        new("ASX:STW", "ASX 200 (STW)"),
        new("ASX:A200", "ASX 200 (A200)"),
        new("ASX:IOZ", "ASX 200 (IOZ)"),
        new("ASX:VAS", "ASX 300 (VAS)"),
    ];

    /// <summary>
    /// Reads "INDEX:XJO", "ASX:STW" or a bare code (taken as ASX). Null for anything blank or odd.
    /// </summary>
    public static (string Exchange, string Code)? Parse(string? key) => Prices.QuoteService.ParseSymbol(key);

    /// <summary>Lines the benchmark up with the portfolio's days, rebased to the value on the day before From.</summary>
    public static BenchmarkResult Compare(BenchmarkChoice choice, IReadOnlyList<BenchmarkPoint> points, DateOnly from, DateOnly to)
    {
        var sorted = points.Where(p => p.Value > 0).OrderBy(p => p.Date).ToArray();
        var dates = sorted.Select(p => p.Date).ToArray();

        double? At(DateOnly day)
        {
            var i = Array.BinarySearch(dates, day);
            if (i < 0)
                i = ~i - 1;
            return i >= 0 ? sorted[i].Value : null;
        }

        // Rebase on the day before the period, or the first value inside it when the history starts later
        var start = At(from.AddDays(-1)) ?? sorted.FirstOrDefault(p => p.Date >= from && p.Date <= to)?.Value;
        if (start is null)
            return new BenchmarkResult(choice, null, [], []);

        var days = new List<(DateOnly, double)>();
        for (var d = from; d <= to; d = d.AddDays(1))
            days.Add((d, At(d) is { } v && d >= FirstUsable(sorted, from) ? v / start.Value - 1 : 0));

        var months = new List<PeriodReturn>();
        double monthStart = start.Value;
        foreach (var g in days.GroupBy(d => (d.Item1.Year, d.Item1.Month)))
        {
            var end = At(g.Last().Item1) ?? monthStart;
            months.Add(new PeriodReturn(g.First().Item1, g.Last().Item1, end / monthStart - 1));
            monthStart = end;
        }

        return new BenchmarkResult(choice, days[^1].Item2, days, months);
    }

    private static DateOnly FirstUsable(BenchmarkPoint[] sorted, DateOnly from) =>
        sorted.Length > 0 && sorted[0].Date > from ? sorted[0].Date : from;
}
