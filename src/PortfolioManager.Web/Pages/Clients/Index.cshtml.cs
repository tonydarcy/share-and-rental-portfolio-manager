using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Clients;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Clients;

/// <summary>The practice view: every portfolio the login may open, as a client.</summary>
public class IndexModel(CurrentPortfolio current, ClientService clients) : PageModel
{
    public IReadOnlyList<ClientListItem> Clients { get; private set; } = [];
    public PortfolioSummary? Selected { get; private set; }
    public bool HasPractice { get; private set; }

    public async Task OnGetAsync()
    {
        var accessible = await current.GetAccessibleAsync();
        Selected = await current.GetSelectedAsync();
        var loginId = CurrentPortfolio.UserId(User);
        Clients = await clients.ListAsync(loginId, accessible.Select(p => p.PortfolioId).ToHashSet());
        HasPractice = await clients.GetPracticeAsync(loginId) is not null;
    }
}
