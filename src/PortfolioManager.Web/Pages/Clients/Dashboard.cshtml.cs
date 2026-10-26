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
    [BindProperty(SupportsGet = true)] public string? Country { get; set; }

    public int Year { get; private set; }
    public IReadOnlyList<int> Years { get; } = FinancialYear.Recent(DateOnly.FromDateTime(DateTime.Today), 6);
    public IReadOnlyList<PracticeClientYear> Rows { get; private set; } = [];
    public IReadOnlyDictionary<string, int> Counts { get; private set; } = new Dictionary<string, int>();
    public IReadOnlyList<string> CountryNames { get; private set; } = [];
    public IReadOnlyList<IGrouping<string, PracticeClientYear>> Groups { get; private set; } = [];
    public DateOnly Today { get; } = DateOnly.FromDateTime(DateTime.Today);

    public async Task OnGetAsync()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        Year = Fy is { } y && Years.Contains(y) ? y : FinancialYear.LastCompleted(today);
        var accessible = await current.GetAccessibleAsync();
        var all = await dashboard.GetAsync(CurrentPortfolio.UserId(User), accessible.Select(p => p.PortfolioId).ToHashSet(), Year);
        Counts = all.GroupBy(r => r.Status).ToDictionary(g => g.Key, g => g.Count());
        CountryNames = all.Select(r => r.CountryName).Distinct().Order(StringComparer.CurrentCultureIgnoreCase).ToList();
        IEnumerable<PracticeClientYear> rows = all;
        if (Status is { Length: > 0 } s)
            rows = rows.Where(r => r.Status == s);
        if (Country is { Length: > 0 } c)
            rows = rows.Where(r => r.CountryName == c);
        Rows = rows.ToList();
        Groups = PracticeDashboardService.ByCountry(Rows);
    }
}
