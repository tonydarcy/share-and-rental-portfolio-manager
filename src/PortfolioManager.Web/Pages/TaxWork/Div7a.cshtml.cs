using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Tax;
using PortfolioManager.Core.TaxWork;

namespace PortfolioManager.Web.Pages.TaxWork;

/// <summary>Division 7A loan calculator: minimum yearly repayments for a complying loan. Nothing is saved.</summary>
public class Div7aModel : PageModel
{
    [BindProperty(SupportsGet = true)]
    public decimal? Balance { get; set; }

    /// <summary>The income year of the first repayment (the year after the loan was made).</summary>
    [BindProperty(SupportsGet = true)]
    public int? FirstYear { get; set; }

    [BindProperty(SupportsGet = true)]
    public int Term { get; set; } = 7;

    /// <summary>The rate for years whose benchmark rate isn't on file yet.</summary>
    [BindProperty(SupportsGet = true)]
    public decimal? Rate { get; set; }

    public IReadOnlyList<Div7aYear> Schedule { get; private set; } = [];

    public void OnGet()
    {
        FirstYear ??= FinancialYear.EndYearFor(DateOnly.FromDateTime(DateTime.Today));
        Term = Term == 25 ? 25 : 7;
        if (Balance is > 0 && FirstYear is >= 2000 and <= 2100)
            Schedule = Div7a.Schedule(Balance.Value, FirstYear.Value, Term, Rate);
    }
}
