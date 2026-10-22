using Microsoft.AspNetCore.Mvc;
using PortfolioManager.Core.Performance;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Reports;

public class PerformanceModel(CurrentPortfolio current, PerformanceService performance) : PeriodReportModel
{
    /// <summary>The longest period worked out day by day (about 15 years).</summary>
    public const int MaxDays = 5500;

    /// <summary>Benchmark to compare with: "INDEX:XJO", "ASX:STW", or "none".</summary>
    [BindProperty(SupportsGet = true)]
    public string? Bm { get; set; }

    public PortfolioSummary? Portfolio { get; private set; }
    public PerformanceResult? Result { get; private set; }
    public IReadOnlyList<BenchmarkChoice> Choices { get; private set; } = [];
    public BenchmarkResult? BenchmarkResult { get; private set; }
    public string? Notice { get; private set; }

    public async Task OnGetAsync()
    {
        ResolvePeriod();
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is null)
            return;

        var from = Period.From;
        var to = Period.To > Today ? Today : Period.To;
        if (to < from)
        {
            Notice = "That period hasn't started yet.";
            return;
        }
        if (to.DayNumber - from.DayNumber > MaxDays)
        {
            from = to.AddDays(-MaxDays);
            Notice = $"Long periods are limited to about 15 years, so this starts on {from:d MMM yyyy}.";
        }

        Result = await performance.GetAsync(Portfolio.PortfolioId, from, to);
        Choices = await performance.GetChoicesAsync();

        // Default to the first index or ETF on file; "none" turns the comparison off
        Bm = string.IsNullOrWhiteSpace(Bm) ? Choices.FirstOrDefault()?.Key : Bm.Trim();
        if (Bm is not null && !Bm.Equals("none", StringComparison.OrdinalIgnoreCase))
            BenchmarkResult = await performance.GetBenchmarkAsync(Bm, Choices, from, to);
    }

    /// <summary>Daily points for the charts, thinned to weekly for long periods so the page stays light.</summary>
    public IEnumerable<int> ChartIndexes()
    {
        var count = Result?.Days.Count ?? 0;
        var step = count > 800 ? 7 : 1;
        for (var i = 0; i < count; i += step)
            yield return i;
        if (count > 0 && (count - 1) % step != 0)
            yield return count - 1;
    }

    public static string Pct(double? value) => value is { } v ? (v * 100).ToString("0.00") + "%" : "–";

    public static string PctClass(double? value) => value > 0 ? "text-success" : value < 0 ? "text-danger" : "";
}
