using Microsoft.AspNetCore.Mvc;
using PortfolioManager.Core.Accounting;
using PortfolioManager.Web.Pages.Shared;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Accounting;

/// <summary>Up to two tracking categories (Department, Property, Project) and their options, to tag journal lines with.</summary>
public class TrackingModel(CurrentPortfolio current, TrackingService tracking, ILogger<TrackingModel> logger) : BooksPageModel(current, logger)
{
    public TrackingSetup Setup { get; private set; } = TrackingSetup.None;

    public async Task OnGetAsync()
    {
        if (await LoadPortfolioAsync())
            Setup = await tracking.GetAsync(PortfolioId);
    }

    public async Task<IActionResult> OnPostCategoryAsync(int categoryId, string? name, bool active = true) =>
        await LoadPortfolioAsync()
            ? Done(await RunAsync(() => tracking.SaveCategoryAsync(PortfolioId, categoryId, name, active)), "Category saved.")
            : Forbid();

    public async Task<IActionResult> OnPostOptionAsync(int optionId, int categoryId, string? name, bool active = true) =>
        await LoadPortfolioAsync()
            ? Done(await RunAsync(() => tracking.SaveOptionAsync(PortfolioId, optionId, categoryId, name, active)), "Option saved.")
            : Forbid();

    public async Task<IActionResult> OnPostDeleteOptionAsync(int optionId) =>
        await LoadPortfolioAsync()
            ? Done(await RunAsync(() => tracking.DeleteOptionAsync(PortfolioId, optionId)), "Option deleted.")
            : Forbid();
}
