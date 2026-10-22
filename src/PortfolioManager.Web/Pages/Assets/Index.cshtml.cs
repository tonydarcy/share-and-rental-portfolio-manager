using PortfolioManager.Core.Assets;
using PortfolioManager.Web.Pages.Shared;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Assets;

/// <summary>The fixed asset register: cost, depreciation to date and book value of each asset.</summary>
public class IndexModel(CurrentPortfolio current, FixedAssetService assets, ILogger<IndexModel> logger) : BooksPageModel(current, logger)
{
    public IReadOnlyList<FixedAsset> Assets { get; private set; } = [];
    public IReadOnlyList<DepreciationRun> Runs { get; private set; } = [];

    public async Task OnGetAsync()
    {
        if (!await LoadPortfolioAsync())
            return;
        Assets = await assets.GetAsync(PortfolioId);
        Runs = await assets.GetRunsAsync(PortfolioId);
    }
}
