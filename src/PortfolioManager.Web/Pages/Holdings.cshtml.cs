using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Holdings;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages;

public class HoldingsModel(CurrentPortfolio current, HoldingsService holdings) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public int? Group { get; set; }

    [BindProperty(SupportsGet = true)]
    public bool IncludeSold { get; set; }

    public PortfolioSummary? Portfolio { get; private set; }
    public IReadOnlyList<CustomGroup> Groups { get; private set; } = [];
    public HoldingsSummary Summary { get; private set; } = new([], new(0, 0, 0, 0));

    public async Task OnGetAsync()
    {
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is null)
            return;

        Groups = await holdings.GetGroupsAsync(Portfolio.PortfolioId);
        // Only filter by a group that belongs to this portfolio
        if (Groups.All(g => g.GroupId != Group))
            Group = null;

        Summary = await holdings.GetHoldingsAsync(Portfolio.PortfolioId, Group, openOnly: !IncludeSold);
    }

    public static string ProfitClass(decimal value) => value > 0 ? "text-success" : value < 0 ? "text-danger" : "";
}
