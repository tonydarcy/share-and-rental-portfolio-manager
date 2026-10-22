using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Core.Statements;
using PortfolioManager.Core.Tax;
using PortfolioManager.Core.TaxWork;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.TaxWork;

/// <summary>
/// A tax page for the selected portfolio and a financial year (?fy=2026, default the last completed
/// year). LoadAsync builds the workpaper; a post that can't find the portfolio is refused.
/// </summary>
public abstract class TaxYearPageModel(CurrentPortfolio current, TaxWorkpaperService workpapers, LockedPeriodService locks) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public int? Fy { get; set; }

    public int Year { get; private set; }
    public DateOnly YearEnd => FinancialYear.EndDate(Year);
    public PortfolioSummary? Portfolio { get; private set; }
    public TaxWorkpaper? Workpaper { get; private set; }
    public bool Locked { get; private set; }

    public static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);

    public IReadOnlyList<int> Years
    {
        get
        {
            var years = FinancialYear.Recent(Today).ToList();
            if (!years.Contains(Year))
                years.Add(Year);
            return years;
        }
    }

    /// <summary>Null when the page can be shown; otherwise the result to return.</summary>
    protected async Task<IActionResult?> LoadAsync()
    {
        Year = StatementFormat.ClampYear(Fy, FinancialYear.LastCompleted(Today));
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is null)
            return HttpMethods.IsGet(Request.Method) ? Page() : Forbid();
        Workpaper = await workpapers.BuildAsync(Portfolio.PortfolioId, Year);
        if (Workpaper is null)
            return NotFound();
        Locked = (await locks.GetAsync(Portfolio.PortfolioId)).IsYearLocked(Year);
        return null;
    }

    protected IActionResult Done(Core.Data.ProcResult result, string message)
    {
        if (result.Ok)
            TempData["Message"] = message;
        else
            TempData["Error"] = result.Error;
        return RedirectToPage(new { fy = Year });
    }
}
