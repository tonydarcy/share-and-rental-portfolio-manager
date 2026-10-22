using Microsoft.AspNetCore.Mvc;
using PortfolioManager.Core.Assets;
using PortfolioManager.Web.Pages.Shared;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Assets;

/// <summary>Post depreciation for every asset up to a date (monthly, quarterly or yearly), and remove the latest run.</summary>
public class DepreciateModel(CurrentPortfolio current, FixedAssetService assets, ILogger<DepreciateModel> logger) : BooksPageModel(current, logger)
{
    [BindProperty(SupportsGet = true)]
    public DateTime? To { get; set; }

    public IReadOnlyList<DepreciationLine> Lines { get; private set; } = [];
    public IReadOnlyList<DepreciationRun> Runs { get; private set; } = [];

    public async Task OnGetAsync()
    {
        if (!await LoadAsync())
            return;
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!await LoadAsync())
            return Forbid();
        return Done(await RunAsync(() => assets.PostRunAsync(PortfolioId, DateOnly.FromDateTime(To!.Value), Core.Regions.RegionSettings.Current.YearStart)),
            $"Depreciation posted to {To:d}.");
    }

    public async Task<IActionResult> OnPostDeleteAsync(int runId)
    {
        if (!await LoadAsync())
            return Forbid();
        return Done(await RunAsync(() => assets.DeleteRunAsync(PortfolioId, runId)), "Depreciation run deleted and its journal removed.");
    }

    private async Task<bool> LoadAsync()
    {
        if (!await LoadPortfolioAsync())
            return false;
        // The last day of last month, by default
        To ??= new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddDays(-1);
        Runs = await assets.GetRunsAsync(PortfolioId);
        Lines = FixedAssetRules.Run(await assets.GetAsync(PortfolioId), DateOnly.FromDateTime(To.Value), Core.Regions.RegionSettings.Current.YearStart);
        return true;
    }
}
