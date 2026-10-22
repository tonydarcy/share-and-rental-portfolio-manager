using Microsoft.AspNetCore.Mvc;
using PortfolioManager.Core.Bills;
using PortfolioManager.Web.Pages.Shared;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Bills;

/// <summary>The people and businesses that send this entity bills: add, change and remove them.</summary>
public class SuppliersModel(CurrentPortfolio current, BillService bills, DocumentLists lists, ILogger<SuppliersModel> logger) : BooksPageModel(current, logger)
{
    [BindProperty(SupportsGet = true)]
    public int? Id { get; set; }

    /// <summary>"bill" goes back to a new bill for the supplier once it is added.</summary>
    [BindProperty(SupportsGet = true)]
    public string? ReturnTo { get; set; }

    [BindProperty]
    public SupplierInput Input { get; set; } = new();

    public IReadOnlyList<Supplier> Suppliers { get; private set; } = [];
    public DocumentListSet Lists { get; private set; } = DocumentLists.Empty;

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await LoadAsync())
            return Page();
        if (Id is { } id && Suppliers.FirstOrDefault(s => s.SupplierId == id) is { } supplier)
            Input = SupplierInput.From(supplier);
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        if (!await LoadAsync())
            return Forbid();
        Input.SupplierId = Id;
        var result = await RunAsync(() => bills.SaveSupplierAsync(PortfolioId, Input));
        if (!result.Ok)
        {
            ModelState.AddModelError(string.Empty, result.Error!);
            return Page();
        }
        TempData["Message"] = Id is null ? $"{Input.Name!.Trim()} added." : "Supplier saved.";
        return ReturnTo == "bill" && Id is null
            ? RedirectToPage("/Bills/Edit", new { supplierId = result.Id })
            : RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(int supplierId)
    {
        if (!await LoadAsync())
            return Forbid();
        return Done(await RunAsync(() => bills.DeleteSupplierAsync(PortfolioId, supplierId)), "Supplier deleted.", new { id = (int?)null });
    }

    private async Task<bool> LoadAsync()
    {
        if (!await LoadPortfolioAsync())
            return false;
        Suppliers = await bills.GetSuppliersAsync(PortfolioId);
        Lists = await lists.LoadAsync(PortfolioId, purchases: true);
        return true;
    }
}
