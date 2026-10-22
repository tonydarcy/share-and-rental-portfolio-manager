using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Core.Statements;
using PortfolioManager.Core.Tax;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Accounting;

/// <summary>Financial statements for the selected portfolio and a financial year (?fy=2026).</summary>
public class StatementsModel(CurrentPortfolio current, FinancialStatementsService statements, LockedPeriodService locks) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public int? Fy { get; set; }

    public int Year { get; private set; }
    public PortfolioSummary? Portfolio { get; private set; }
    public FinancialStatements? Statements { get; private set; }
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

    public async Task<IActionResult> OnGetAsync()
    {
        Year = StatementFormat.ClampYear(Fy, FinancialYear.LastCompleted(Today));
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is null)
            return Page();

        Statements = await statements.BuildAsync(Portfolio.PortfolioId, Year);
        Locked = (await locks.GetAsync(Portfolio.PortfolioId)).IsYearLocked(Year);
        return Statements is null ? NotFound() : Page();
    }
}
