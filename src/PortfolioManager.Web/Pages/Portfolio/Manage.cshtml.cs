using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Portfolio;

// Creates a new portfolio owned by the login
[AllowViewOnly]
public class ManageModel(CurrentPortfolio current, PortfolioService portfolios) : PageModel
{
    [BindProperty]
    public string? Name { get; set; }

    [BindProperty]
    public string EntityType { get; set; } = PortfolioService.EntityTypes[0];

    public IReadOnlyList<PortfolioSummary> Portfolios { get; private set; } = [];
    public PortfolioSummary? Selected { get; private set; }

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostAsync()
    {
        foreach (var error in PortfolioService.ValidateNew(Name, EntityType))
            ModelState.AddModelError(string.Empty, error);
        if (!ModelState.IsValid)
        {
            await LoadAsync();
            return Page();
        }

        var before = (await current.GetAccessibleAsync()).Select(p => p.PortfolioId).ToHashSet();
        // Always owned by the signed-in login, so it shows in their portfolio picker
        await portfolios.CreateAsync(CurrentPortfolio.UserId(User), Name!, EntityType);

        current.Refresh();
        var added = (await current.GetAccessibleAsync()).Where(p => !before.Contains(p.PortfolioId)).ToList();
        // Switch to the new portfolio when it can be told apart
        if (added.Count == 1)
            await current.TrySelectAsync(added[0].PortfolioId);

        TempData["Message"] = $"Created the portfolio \"{Name!.Trim()}\"" + (added.Count == 1 ? " and switched to it." : ".");
        return RedirectToPage();
    }

    private async Task LoadAsync()
    {
        Portfolios = await current.GetAccessibleAsync();
        Selected = await current.GetSelectedAsync();
    }
}
