using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Data;
using PortfolioManager.Core.Invoicing;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Core.SalesTax;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Invoices;

/// <summary>Raise a new invoice or change one. Saving as a draft posts nothing; approving posts it to the ledger.</summary>
public class EditModel(CurrentPortfolio current, InvoiceService invoices, DocumentLists documentLists, ILogger<EditModel> logger) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public int? Id { get; set; }

    /// <summary>Picks the customer on a new invoice, e.g. one just added.</summary>
    [BindProperty(SupportsGet = true)]
    public int? CustomerId { get; set; }

    [BindProperty]
    public InvoiceInput Input { get; set; } = new();

    public PortfolioSummary? Portfolio { get; private set; }
    public InvoiceDetail? Existing { get; private set; }
    public IReadOnlyList<Customer> Customers { get; private set; } = [];
    public DocumentListSet Lists { get; private set; } = DocumentLists.Empty;
    public IReadOnlyList<LedgerAccount> IncomeAccounts => Lists.Accounts;
    public IReadOnlyList<TaxCode> TaxCodes => Lists.TaxCodes;
    public string TaxName => Lists.TaxName;

    public async Task<IActionResult> OnGetAsync()
    {
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is null)
            return Page();
        if (!Portfolio.CanEdit)
            return RedirectToPage("/Invoices/Index");

        await LoadListsAsync();
        if (Id is { } id)
        {
            Existing = await invoices.GetInvoiceAsync(Portfolio.PortfolioId, id);
            if (Existing is null || Existing.IsVoid)
            {
                TempData["Error"] = Existing is null ? "That invoice wasn't found in this portfolio." : "A voided invoice can't be changed.";
                return RedirectToPage("/Invoices/Index");
            }
            Input = InvoiceInput.From(Existing);
        }
        else
        {
            var settings = await invoices.GetSettingsAsync(Portfolio.PortfolioId);
            var defaultAccount = IncomeAccounts.FirstOrDefault(a => a.Number == 4800) ?? IncomeAccounts.FirstOrDefault();
            Input = InvoiceInput.New(DateTime.Today, settings, defaultAccount?.AccountId ?? 0);
            if (CustomerId is { } customerId)
                Input.CustomerId = customerId;
        }
        AddBlankLine();
        return Page();
    }

    public Task<IActionResult> OnPostDraftAsync() => SaveAsync(approve: false);

    public Task<IActionResult> OnPostApproveAsync() => SaveAsync(approve: true);

    private async Task<IActionResult> SaveAsync(bool approve)
    {
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is null)
            return Forbid();
        Input.Lines ??= [];
        await LoadListsAsync();
        if (Id is { } id)
            Existing = await invoices.GetInvoiceAsync(Portfolio.PortfolioId, id);

        ProcResult result;
        try
        {
            result = await invoices.SaveInvoiceAsync(Portfolio.PortfolioId, Id, Input, approve,
                IncomeAccounts.Select(a => a.AccountId).ToHashSet());
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Saving invoice {InvoiceId} failed for portfolio {PortfolioId}", Id, Portfolio.PortfolioId);
            ModelState.AddModelError(string.Empty, LockedPeriodService.IsLockError(ex) ? LockedPeriodFilter.Message(ex) : "The invoice couldn't be saved, and nothing was changed. Try again.");
            AddBlankLine();
            return Page();
        }

        if (!result.Ok)
        {
            ModelState.AddModelError(string.Empty, result.Error!);
            AddBlankLine();
            return Page();
        }
        TempData["Message"] = approve && Existing?.IsDraft != false
            ? "Invoice approved and posted to the ledger."
            : Existing is { IsDraft: false } ? "Invoice saved and its journal updated." : "Draft saved. Nothing is posted until it is approved.";
        return RedirectToPage("/Invoices/View", new { id = result.Id });
    }

    private async Task LoadListsAsync()
    {
        var portfolioId = Portfolio!.PortfolioId;
        Customers = (await invoices.GetCustomersAsync(portfolioId)).Where(c => c.IsActive || c.CustomerId == Input.CustomerId).ToList();
        Lists = await documentLists.LoadAsync(portfolioId, purchases: false);
    }

    private void AddBlankLine()
    {
        if (Input.Lines.Count == 0 || !Input.Lines[^1].IsBlank)
            Input.Lines.Add(new InvoiceLineInput { Quantity = 1, AccountId = Input.Lines.LastOrDefault()?.AccountId ?? IncomeAccounts.FirstOrDefault()?.AccountId ?? 0 });
    }
}
