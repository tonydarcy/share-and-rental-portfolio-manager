using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Core.Prices;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Prices;

public class TrendsModel(CurrentPortfolio current, QuoteService quotes) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public TrendScope Scope { get; set; } = TrendScope.Holdings;

    public PortfolioSummary? Portfolio { get; private set; }
    public IReadOnlyList<TrendRow> Rows { get; private set; } = [];

    public async Task OnGetAsync()
    {
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is null)
            return;
        if (!Enum.IsDefined(Scope))
            Scope = TrendScope.Holdings;
        Rows = await quotes.GetTrendsAsync(Portfolio.PortfolioId, Scope);
    }

    public int Count(string status) => Rows.Count(r => r.Trend.Status == status);
}
