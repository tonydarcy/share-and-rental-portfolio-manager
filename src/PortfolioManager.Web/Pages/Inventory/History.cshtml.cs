using Microsoft.AspNetCore.Mvc;
using PortfolioManager.Core.Inventory;
using PortfolioManager.Web.Pages.Shared;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Inventory;

/// <summary>Every stock movement in and out, for one item or all, with the running quantity.</summary>
public class HistoryModel(CurrentPortfolio current, InventoryService inventory, ILogger<HistoryModel> logger) : BooksPageModel(current, logger)
{
    [BindProperty(SupportsGet = true)]
    public int? Item { get; set; }

    public IReadOnlyList<InventoryItem> Items { get; private set; } = [];
    public IReadOnlyList<StockMovement> Movements { get; private set; } = [];

    public async Task OnGetAsync()
    {
        if (!await LoadPortfolioAsync())
            return;
        Items = await inventory.GetItemsAsync(PortfolioId);
        Movements = await inventory.GetMovementsAsync(PortfolioId, Item);
    }
}
