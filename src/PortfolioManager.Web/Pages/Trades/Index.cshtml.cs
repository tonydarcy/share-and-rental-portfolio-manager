using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Core.Trades;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Trades;

public class IndexModel(CurrentPortfolio current, TradesService trades) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? Code { get; set; }

    [BindProperty(SupportsGet = true)]
    public bool OldestFirst { get; set; }

    [TempData]
    public string? Message { get; set; }

    public PortfolioSummary? Portfolio { get; private set; }
    public IReadOnlyList<string> Codes { get; private set; } = [];
    public IReadOnlyList<ShareTrade> Trades { get; private set; } = [];

    public async Task OnGetAsync()
    {
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is null)
            return;

        Codes = await trades.GetStockCodesAsync(Portfolio.PortfolioId);
        // Only filter by a code this portfolio has actually traded
        Code = Codes.FirstOrDefault(c => string.Equals(c, Code?.Trim(), StringComparison.OrdinalIgnoreCase));

        Trades = await trades.GetTradesAsync(Portfolio.PortfolioId, Code, OldestFirst);
    }
}
