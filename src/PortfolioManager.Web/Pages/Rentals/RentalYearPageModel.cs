using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Core.Statements;
using PortfolioManager.Core.Tax;
using PortfolioManager.Core.TaxWork;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Rentals;

/// <summary>A rental property report for the selected portfolio and a financial year (?fy=2026, default the last completed year).</summary>
public abstract class RentalYearPageModel(CurrentPortfolio current, RentalService rentals) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public int? Fy { get; set; }

    public int Year { get; private set; }
    public PortfolioSummary? Portfolio { get; private set; }
    public RentalSchedule Schedule { get; private set; } = RentalSchedule.None;

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

    protected RentalService Rentals => rentals;

    /// <summary>Picks the year and portfolio and loads the year's schedule; false when there is no portfolio.</summary>
    protected async Task<bool> LoadAsync(bool allProperties = false)
    {
        Year = StatementFormat.ClampYear(Fy, FinancialYear.LastCompleted(Today));
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is null)
            return false;
        Schedule = await rentals.GetYearAsync(Portfolio.PortfolioId, Year, allProperties);
        return true;
    }
}
