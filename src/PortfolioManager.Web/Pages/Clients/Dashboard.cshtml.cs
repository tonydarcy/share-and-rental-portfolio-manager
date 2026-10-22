using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Practice;
using PortfolioManager.Core.Tax;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Clients;

/// <summary>Every client the login can open with the year-end status and what is still outstanding. Nothing is changed here.</summary>
public class DashboardModel(CurrentPortfolio current, PracticeDashboardService dashboard) : PageModel
{
    [BindProperty(SupportsGet = true)] public int? Fy { get; set; }
    [BindProperty(SupportsGet = true)] public string? Status { get; set; }

    public int Year { get; private set; }
    public IReadOnlyList<int> Years { get; } = FinancialYear.Recent(DateOnly.FromDateTime(DateTime.Today), 6);
    public IReadOnlyList<PracticeClientYear> Rows { get; private set; } = [];
    public IReadOnlyDictionary<string, int> Counts { get; private set; } = new Dictionary<string, int>();

    public async Task OnGetAsync()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        Year = Fy is { } y && Years.Contains(y) ? y : FinancialYear.LastCompleted(today);
        var accessible = await current.GetAccessibleAsync();
        var all = await dashboard.GetAsync(CurrentPortfolio.UserId(User), accessible.Select(p => p.PortfolioId).ToHashSet(), Year);
        Counts = all.GroupBy(r => r.Status).ToDictionary(g => g.Key, g => g.Count());
        Rows = Status is { Length: > 0 } s ? all.Where(r => r.Status == s).ToList() : all;
    }
}
