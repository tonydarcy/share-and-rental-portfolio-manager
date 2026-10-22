using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Core.Reports;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Reports;

public class DiversityModel(CurrentPortfolio current, DiversityService diversity, LookThroughService lookThrough) : PageModel
{
    public PortfolioSummary? Portfolio { get; private set; }
    public DiversityReport Report { get; private set; } = new([], [], 0);
    public LookThroughReport? LookThrough { get; private set; }

    public async Task OnGetAsync()
    {
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is null)
            return;
        Report = await diversity.GetAsync(Portfolio.PortfolioId);
        try
        {
            LookThrough = await lookThrough.GetAsync(Portfolio.PortfolioId);
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208)
        {
            // The look-through migration hasn't been run
        }
    }
}
