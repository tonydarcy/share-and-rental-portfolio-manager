using Microsoft.AspNetCore.Mvc;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Core.Reports;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Reports;

/// <summary>
/// One view across a family group's portfolios. Groups belong to the login (so view access is enough), and only
/// portfolios the login can still open are shown or saved.
/// </summary>
[AllowViewOnly]
public class ConsolidatedModel(CurrentPortfolio current, ConsolidatedService consolidated) : PeriodReportModel
{
    [BindProperty(SupportsGet = true)] public int? Group { get; set; }

    [BindProperty] public string? Name { get; set; }
    [BindProperty] public List<int> Members { get; set; } = [];

    [TempData] public string? Success { get; set; }

    public IReadOnlyList<PortfolioSummary> Accessible { get; private set; } = [];
    public IReadOnlyList<FamilyGroup> Groups { get; private set; } = [];
    public FamilyGroup? Selected { get; private set; }
    public ConsolidatedReport? Report { get; private set; }
    public bool Editing { get; private set; }
    public string? LoadError { get; private set; }

    private int LoginId => CurrentPortfolio.UserId(User);

    public async Task OnGetAsync(bool edit = false)
    {
        ResolvePeriod();
        if (!await LoadAsync())
            return;
        Editing = edit || Groups.Count == 0;
        if (Selected is null)
            return;
        if (Editing)
        {
            Name = Selected.Name;
            Members = Selected.PortfolioIds.ToList();
        }
        var members = Accessible.Where(p => Selected.PortfolioIds.Contains(p.PortfolioId)).ToList();
        Report = await consolidated.GetAsync(members, Period);
    }

    public async Task<IActionResult> OnPostSaveAsync(int id)
    {
        ResolvePeriod();
        if (!await LoadAsync())
            return Page();
        var result = await consolidated.SaveAsync(LoginId, id, Name, Members, Accessible.Select(p => p.PortfolioId).ToHashSet());
        if (!result.Ok)
        {
            ModelState.AddModelError(nameof(Name), result.Error!);
            Editing = true;
            Selected = id > 0 ? Groups.FirstOrDefault(g => g.GroupId == id) : null;
            return Page();
        }
        Success = "Group saved.";
        return RedirectToPage(new { group = result.Id });
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var result = await consolidated.DeleteAsync(LoginId, id);
        Success = result.Ok ? "Group deleted." : result.Error;
        return RedirectToPage();
    }

    private async Task<bool> LoadAsync()
    {
        Accessible = await current.GetAccessibleAsync();
        try
        {
            Groups = await consolidated.GetGroupsAsync(LoginId);
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 208 or 2812)
        {
            LoadError = "The consolidated view needs the database update 2026-10-21-05-family-groups.sql. Ask whoever runs this site to apply it.";
            return false;
        }
        Selected = Groups.FirstOrDefault(g => g.GroupId == Group) ?? (Group == 0 ? null : Groups.FirstOrDefault());
        return true;
    }

    public string EntityName(int id) => Accessible.FirstOrDefault(p => p.PortfolioId == id)?.Name ?? "";
}
