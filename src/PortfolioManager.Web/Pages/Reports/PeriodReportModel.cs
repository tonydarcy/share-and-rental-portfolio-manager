using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Tax;

namespace PortfolioManager.Web.Pages.Reports;

/// <summary>
/// A report over a financial year or custom dates, taken from the query string
/// (?fy=2026 or ?from=2025-07-01&amp;to=2026-06-30) so a report can be bookmarked.
/// </summary>
public abstract class PeriodReportModel : PageModel
{
    [BindProperty(SupportsGet = true)]
    public int? Fy { get; set; }

    [BindProperty(SupportsGet = true)]
    public DateOnly? From { get; set; }

    [BindProperty(SupportsGet = true)]
    public DateOnly? To { get; set; }

    public ReportPeriod Period { get; private set; } = null!;

    public static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);

    protected void ResolvePeriod() => Period = FinancialYear.Resolve(Fy, From, To, Today);

    public static string GainClass(decimal value) => value > 0 ? "text-success" : value < 0 ? "text-danger" : "";
}
