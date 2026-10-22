using Microsoft.AspNetCore.Mvc;
using PortfolioManager.Core.Bills;
using PortfolioManager.Core.Invoicing;
using PortfolioManager.Web.Pages.Shared;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Bills;

/// <summary>Enter a new bill or supplier credit, or change one. A draft posts nothing; approving posts it to Accounts Payable.</summary>
public class EditModel(CurrentPortfolio current, BillService bills, DocumentLists lists, ILogger<EditModel> logger) : BooksPageModel(current, logger)
{
    [BindProperty(SupportsGet = true)]
    public int? Id { get; set; }

    [BindProperty(SupportsGet = true)]
    public int? SupplierId { get; set; }

    /// <summary>"credit" starts a supplier credit rather than a bill.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Kind { get; set; }

    [BindProperty]
    public BillInput Input { get; set; } = new();

    public BillDetail? Existing { get; private set; }
    public IReadOnlyList<Supplier> Suppliers { get; private set; } = [];
    public DocumentListSet Lists { get; private set; } = DocumentLists.Empty;

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await LoadAsync())
            return Page();
        if (!CanEdit)
            return RedirectToPage("/Bills/Index");
        if (Id is { } id)
        {
            Existing = await bills.GetBillAsync(PortfolioId, id);
            if (Existing is null || Existing.IsVoid)
            {
                TempData["Error"] = Existing is null ? "That bill wasn't found in this portfolio." : "A voided bill can't be changed.";
                return RedirectToPage("/Bills/Index");
            }
            Input = BillInput.From(Existing);
        }
        else
        {
            var supplier = Suppliers.FirstOrDefault(s => s.SupplierId == SupplierId);
            var account = supplier?.DefaultAccountId ?? Lists.Accounts.FirstOrDefault()?.AccountId ?? 0;
            Input = BillInput.New(DateTime.Today, supplier?.DueDays ?? 30, account);
            Input.AmountsIncludeTax = true;
            Input.Kind = Kind == "credit" ? BillKinds.SupplierCredit : BillKinds.Bill;
            if (supplier is not null)
            {
                Input.SupplierId = supplier.SupplierId;
                Input.Lines[0].TaxCodeId = supplier.DefaultTaxCodeId;
            }
        }
        AddBlankLine();
        return Page();
    }

    public Task<IActionResult> OnPostDraftAsync() => SaveAsync(approve: false);

    public Task<IActionResult> OnPostApproveAsync() => SaveAsync(approve: true);

    private async Task<IActionResult> SaveAsync(bool approve)
    {
        if (!await LoadAsync())
            return Forbid();
        Input.Lines ??= [];
        if (Id is { } id)
            Existing = await bills.GetBillAsync(PortfolioId, id);
        var result = await RunAsync(() => bills.SaveBillAsync(PortfolioId, Id, Input, approve, Lists.AccountIds));
        if (!result.Ok)
        {
            ModelState.AddModelError(string.Empty, result.Error!);
            AddBlankLine();
            return Page();
        }
        TempData["Message"] = approve && Existing?.IsDraft != false
            ? "Bill approved and posted to the ledger."
            : Existing is { IsDraft: false } ? "Bill saved and its journal updated." : "Draft saved. Nothing is posted until it is approved.";
        return RedirectToPage("/Bills/View", new { id = result.Id });
    }

    private async Task<bool> LoadAsync()
    {
        if (!await LoadPortfolioAsync())
            return false;
        Suppliers = (await bills.GetSuppliersAsync(PortfolioId)).Where(s => s.IsActive || s.SupplierId == Input.SupplierId).ToList();
        Lists = await lists.LoadAsync(PortfolioId, purchases: true);
        return true;
    }

    private void AddBlankLine()
    {
        if (Input.Lines.Count == 0 || !Input.Lines[^1].IsBlank)
            Input.Lines.Add(new InvoiceLineInput { Quantity = 1, AccountId = Input.Lines.LastOrDefault()?.AccountId ?? Lists.Accounts.FirstOrDefault()?.AccountId ?? 0 });
    }
}
