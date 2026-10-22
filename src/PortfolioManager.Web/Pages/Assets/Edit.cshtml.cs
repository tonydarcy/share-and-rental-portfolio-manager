using Microsoft.AspNetCore.Mvc;
using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Assets;
using PortfolioManager.Web.Pages.Shared;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Assets;

/// <summary>Add or change an asset, see its depreciation schedule, and record its sale or scrapping.</summary>
public class EditModel(CurrentPortfolio current, FixedAssetService assets, ChartOfAccountsService chart, ILogger<EditModel> logger)
    : BooksPageModel(current, logger)
{
    [BindProperty(SupportsGet = true)]
    public int? Id { get; set; }

    [BindProperty]
    public FixedAssetInput Input { get; set; } = new();

    public FixedAsset? Existing { get; private set; }
    public IReadOnlyList<LedgerAccount> Accounts { get; private set; } = [];
    public IReadOnlyList<AccountChoice> BankAccounts { get; private set; } = [];
    public bool Australia => Core.Regions.RegionSettings.Current.IsAustralia;

    /// <summary>For a sale being worked out: the preview the form shows before it is posted.</summary>
    public DisposalPreview? Preview { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await LoadAsync())
            return Page();
        if (Id is not null && Existing is null)
        {
            TempData["Error"] = "That asset wasn't found in this portfolio.";
            return RedirectToPage("/Assets/Index");
        }
        if (Existing is not null)
            Input = FixedAssetInput.From(Existing);
        else
        {
            var (asset, accum, expense) = await assets.DefaultAccountsAsync(PortfolioId);
            Input = new FixedAssetInput { Purchased = DateTime.Today, AssetAccountId = asset, AccumAccountId = accum, ExpenseAccountId = expense };
        }
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        if (!await LoadAsync())
            return Forbid();
        if (Existing is { } e && (e.Posted > 0 || e.IsDisposed))
        {
            // Those fields are locked on the form (so not posted back): only the name and description change
            var locked = FixedAssetInput.From(e);
            locked.Number = Input.Number;
            locked.Name = Input.Name;
            locked.Description = Input.Description;
            Input = locked;
        }
        var result = await RunAsync(() => assets.SaveAsync(PortfolioId, Id, Input, Accounts.Select(a => a.AccountId).ToHashSet(), Australia));
        if (!result.Ok)
        {
            ModelState.AddModelError(string.Empty, result.Error!);
            return Page();
        }
        return Done(result, Id is null
            ? "Asset added. Its cost isn't posted here: record the purchase as a bill or journal to the asset account."
            : "Asset saved.", new { id = result.Id });
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        if (!await LoadAsync())
            return Forbid();
        var result = await RunAsync(() => assets.DeleteAsync(PortfolioId, Id ?? 0));
        return result.Ok ? Done(result, "Asset deleted.", null, "/Assets/Index") : Done(result, "", new { id = Id });
    }

    public async Task<IActionResult> OnPostDisposeAsync(DateTime? soldOn, decimal proceeds, int? proceedsAccountId, bool confirm)
    {
        if (!await LoadAsync() || Existing is null)
            return Forbid();
        Input = FixedAssetInput.From(Existing);
        var date = DateOnly.FromDateTime(soldOn ?? DateTime.Today);
        if (!confirm)
        {
            Preview = FixedAssetRules.Disposal(Existing, date, proceeds, Core.Regions.RegionSettings.Current.YearStart);
            ViewData["SoldOn"] = date;
            ViewData["ProceedsAccountId"] = proceedsAccountId;
            return Page();
        }
        return Done(await RunAsync(() => assets.DisposeAsync(PortfolioId, Existing, date, proceeds, proceedsAccountId, Core.Regions.RegionSettings.Current.YearStart)),
            "Sale posted: depreciation to the sale date, the asset and its depreciation removed, and the gain or loss.", new { id = Id });
    }

    public async Task<IActionResult> OnPostUndoDisposalAsync()
    {
        if (!await LoadAsync())
            return Forbid();
        return Done(await RunAsync(() => assets.UndoDisposalAsync(PortfolioId, Id ?? 0)), "Sale undone.", new { id = Id });
    }

    /// <summary>Each financial year's depreciation from the engine, for the schedule.</summary>
    public IReadOnlyList<(int Year, decimal Opening, decimal Amount)> Schedule()
    {
        if (Existing is null || Existing.Method == DepreciationMethods.None)
            return [];
        var start = Core.Regions.RegionSettings.Current.YearStart;
        var asset = Existing.ForEngine;
        var rows = new List<(int, decimal, decimal)>();
        var first = Core.Tax.FinancialYear.EndYearFor(asset.OpeningDate ?? asset.Start, start);
        decimal before = Math.Min(asset.OpeningAccum, asset.Cost);
        for (var y = first; y < first + 60 && before < asset.Cost; y++)
        {
            var end = Core.Tax.FinancialYear.EndDate(y, start);
            var total = DepreciationEngine.Accumulated(asset, end, start);
            rows.Add((y, asset.Cost - before, total - before));
            if (total == before && y > first)
                break;
            before = total;
            if (asset.Disposed is { } d && d <= end)
                break;
        }
        return rows;
    }

    private async Task<bool> LoadAsync()
    {
        if (!await LoadPortfolioAsync())
            return false;
        if (Id is { } id)
            Existing = await assets.GetOneAsync(PortfolioId, id);
        Accounts = (await chart.GetAccountsAsync(PortfolioId)).Where(a => !a.IsInactive && a.Number % 1000 != 0).OrderBy(a => a.Number).ToList();
        BankAccounts = await chart.GetImportAccountsAsync(PortfolioId);
        return true;
    }
}
