using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Dashboard;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages;

public class IndexModel(CurrentPortfolio current, DashboardService dashboard, ILogger<IndexModel> logger) : PageModel
{
    public PortfolioSummary? Portfolio { get; private set; }
    public PortfolioValue Value { get; private set; } = new();
    public IReadOnlyList<HistoryPoint> History { get; private set; } = [];
    public IReadOnlyList<(DateTime Month, decimal Amount)> ProjectedIncome { get; private set; } = [];
    public bool HistoryRefreshFailed { get; private set; }

    public async Task OnGetAsync()
    {
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is null)
            return;

        var id = Portfolio.PortfolioId;
        Value = await dashboard.GetValueAsync(id);

        try
        {
            History = await dashboard.GetHistoryAsync(id, DateTime.Today);
        }
        catch (Exception ex)
        {
            // The legacy Dashboard also carries on without the chart if the recalculation fails
            logger.LogWarning(ex, "Could not refresh history for portfolio {PortfolioId}", id);
            HistoryRefreshFailed = true;
        }

        ProjectedIncome = DashboardService.ByMonth(await dashboard.GetProjectedPayoutsAsync(id));
    }
}
