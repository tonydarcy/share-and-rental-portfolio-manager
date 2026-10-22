using Microsoft.AspNetCore.Mvc;
using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Inventory;
using PortfolioManager.Web.Pages.Shared;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Inventory;

/// <summary>Count the stock and post the differences: shrinkage, damage, or stock found.</summary>
public class StocktakeModel(CurrentPortfolio current, InventoryService inventory, ChartOfAccountsService chart, ILogger<StocktakeModel> logger)
    : BooksPageModel(current, logger)
{
    [BindProperty(SupportsGet = true)]
    public DateTime? Date { get; set; }

    [BindProperty]
    public List<StocktakeLine> Lines { get; set; } = [];

    [BindProperty]
    public int AccountId { get; set; }

    [BindProperty]
    public string? Note { get; set; }

    public IReadOnlyList<InventoryItem> Items { get; private set; } = [];
    public IReadOnlyList<LedgerAccount> Accounts { get; private set; } = [];
    public IReadOnlyList<StockAdjustment> Adjustments { get; private set; } = [];

    public async Task OnGetAsync()
    {
        if (!await LoadAsync())
            return;
        AccountId = Accounts.FirstOrDefault(a => a.Number == 5710)?.AccountId ?? Accounts.FirstOrDefault()?.AccountId ?? 0;
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!await LoadAsync())
            return Forbid();
        // What is on hand comes from the books again, not the form
        var onHand = Lines.Where(l => l.Counted is not null).Select(l => new StocktakeLine
        {
            ItemId = l.ItemId, Counted = l.Counted, UnitCost = l.UnitCost,
            OnHand = Items.FirstOrDefault(i => i.ItemId == l.ItemId) is { } item ? StockAt(item) : 0,
        }).Where(l => Items.Any(i => i.ItemId == l.ItemId && i.Tracked)).ToList();
        var result = await RunAsync(() => inventory.AdjustAsync(PortfolioId, DateOnly.FromDateTime(Date!.Value), AccountId, Note, onHand));
        if (!result.Ok)
        {
            ModelState.AddModelError(string.Empty, result.Error!);
            return Page();
        }
        return Done(result, "Stocktake posted.", new { date = Date?.ToString("yyyy-MM-dd") });
    }

    public async Task<IActionResult> OnPostDeleteAsync(int adjustmentId)
    {
        if (!await LoadAsync())
            return Forbid();
        return Done(await RunAsync(() => inventory.DeleteAdjustmentAsync(PortfolioId, adjustmentId)), "Adjustment deleted.");
    }

    private IReadOnlyList<StockMovement> movements = [];

    public decimal StockAt(InventoryItem item) => InventoryRules.OnHandAt(movements, item.ItemId, DateOnly.FromDateTime(Date ?? DateTime.Today));

    private async Task<bool> LoadAsync()
    {
        if (!await LoadPortfolioAsync())
            return false;
        Date ??= DateTime.Today;
        Items = (await inventory.GetItemsAsync(PortfolioId)).Where(i => i.Tracked).ToList();
        movements = await inventory.GetMovementsAsync(PortfolioId);
        Adjustments = await inventory.GetAdjustmentsAsync(PortfolioId);
        Accounts = (await chart.GetAccountsAsync(PortfolioId)).Where(a => !a.IsInactive && a.Number % 1000 != 0 && a.Type is "Expense" or "Revenue")
            .OrderBy(a => a.Number).ToList();
        return true;
    }
}
