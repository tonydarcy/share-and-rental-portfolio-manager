using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Tax;

namespace PortfolioManager.Web.Pages.Accounting;

/// <summary>
/// A report at one date (?asAt=2026-06-30), today unless given, as on the legacy
/// balance sheet and trial balance pages.
/// </summary>
public abstract class AsAtReportModel : PageModel
{
    [BindProperty(SupportsGet = true)]
    public DateOnly? AsAt { get; set; }

    public static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);

    public DateOnly Date => AsAt is { Year: >= 1900 and <= 2100 } d ? d : Today;

    /// <summary>The ends of the last two completed financial years, for the shortcut buttons.</summary>
    public static IReadOnlyList<(string Label, DateOnly Date)> Shortcuts
    {
        get
        {
            var last = FinancialYear.LastCompleted(Today);
            return
            [
                ("Today", Today),
                ($"End of FY {FinancialYear.Label(last)}", FinancialYear.EndDate(last)),
                ($"End of FY {FinancialYear.Label(last - 1)}", FinancialYear.EndDate(last - 1)),
            ];
        }
    }
}
