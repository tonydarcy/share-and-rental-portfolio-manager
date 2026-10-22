using Microsoft.AspNetCore.Mvc;
using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Inventory;
using PortfolioManager.Core.SalesTax;
using PortfolioManager.Web.Pages.Shared;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Inventory;

/// <summary>
/// The items this entity buys and sells. A tracked item keeps a quantity on hand at its average cost: a bill adds stock,
/// an invoice takes it out and posts its cost of goods sold.
/// </summary>
public class IndexModel(CurrentPortfolio current, InventoryService inventory, ChartOfAccountsService chart, SalesTaxService salesTax,
    ILogger<IndexModel> logger) : BooksPageModel(current, logger)
{
    [BindProperty(SupportsGet = true)]
    public int? Id { get; set; }

    [BindProperty]
    public ItemInput Input { get; set; } = new();

    public IReadOnlyList<InventoryItem> Items { get; private set; } = [];
    public IReadOnlyList<LedgerAccount> Accounts { get; private set; } = [];
    public IReadOnlyList<TaxCode> TaxCodes { get; private set; } = [];
    public decimal StockValue => Items.Where(i => i.Tracked).Sum(i => i.TotalCost);

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await LoadAsync())
            return Page();
        if (Id is { } id && Items.FirstOrDefault(i => i.ItemId == id) is { } item)
            Input = ItemInput.From(item);
        else
        {
            int? Number(int n) => Accounts.FirstOrDefault(a => a.Number == n)?.AccountId;
            Input = new ItemInput { InventoryAccountId = Number(1300), CogsAccountId = Number(5700), SalesAccountId = Number(4800) };
        }
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        if (!await LoadAsync())
            return Forbid();
        Input.ItemId = Id ?? 0;
        var result = await RunAsync(() => inventory.SaveItemAsync(PortfolioId, Input));
        if (!result.Ok)
        {
            ModelState.AddModelError(string.Empty, result.Error!);
            return Page();
        }
        return Done(result, Id is null ? "Item added." : "Item saved.", new { id = (int?)null });
    }

    public async Task<IActionResult> OnPostDeleteAsync(int itemId)
    {
        if (!await LoadAsync())
            return Forbid();
        return Done(await RunAsync(() => inventory.DeleteItemAsync(PortfolioId, itemId)), "Item deleted.", new { id = (int?)null });
    }

    private async Task<bool> LoadAsync()
    {
        if (!await LoadPortfolioAsync())
            return false;
        Items = await inventory.GetItemsAsync(PortfolioId);
        Accounts = (await chart.GetAccountsAsync(PortfolioId)).Where(a => !a.IsInactive && a.Number % 1000 != 0).OrderBy(a => a.Number).ToList();
        TaxCodes = await salesTax.GetActiveCodesAsync(PortfolioId);
        return true;
    }
}
