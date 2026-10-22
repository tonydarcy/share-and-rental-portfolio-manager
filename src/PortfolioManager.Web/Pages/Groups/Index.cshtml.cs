using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Groups;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Groups;

public class IndexModel(CurrentPortfolio current, GroupService groups) : PageModel
{
    [BindProperty]
    public string? Name { get; set; }

    public PortfolioSummary? Portfolio { get; private set; }
    public IReadOnlyList<GroupSummary> Groups { get; private set; } = [];

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostCreateAsync()
    {
        if (await current.GetSelectedAsync() is not { } portfolio)
            return RedirectToPage();
        if (GroupService.ValidateName(Name) is { } error)
        {
            ModelState.AddModelError(nameof(Name), error);
            await LoadAsync();
            return Page();
        }

        var result = await groups.CreateAsync(portfolio.PortfolioId, Name!);
        if (!result.Ok)
        {
            ModelState.AddModelError(nameof(Name), result.Error!);
            await LoadAsync();
            return Page();
        }
        TempData["Message"] = $"Created the group \"{Name!.Trim()}\". Tick the trades that belong in it.";
        return RedirectToPage("Edit", new { id = result.Id });
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        if (await current.GetSelectedAsync() is { } portfolio)
        {
            var result = await groups.DeleteAsync(portfolio.PortfolioId, id);
            TempData[result.Ok ? "Message" : "Error"] = result.Ok ? "Deleted the group. Its trades are unchanged." : result.Error;
        }
        return RedirectToPage();
    }

    private async Task LoadAsync()
    {
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is not null)
            Groups = await groups.GetGroupsAsync(Portfolio.PortfolioId);
    }
}
