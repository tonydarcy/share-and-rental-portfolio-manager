using Microsoft.AspNetCore.Mvc;
using PortfolioManager.Core.Bills;
using PortfolioManager.Core.Invoicing;
using PortfolioManager.Core.Orders;
using PortfolioManager.Web.Pages.Shared;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Orders;

/// <summary>Write a quote or a purchase order, or change one that hasn't become an invoice or bill.</summary>
public class EditModel(CurrentPortfolio current, QuoteOrderService orders, InvoiceService invoices, BillService bills, DocumentLists lists,
    ILogger<EditModel> logger) : BooksPageModel(current, logger)
{
    [BindProperty(SupportsGet = true)]
    public int? Id { get; set; }

    [BindProperty(SupportsGet = true)]
    public string Kind { get; set; } = QuoteOrderKinds.Quote;

    [BindProperty]
    public QuoteOrderInput Input { get; set; } = new();

    public QuoteOrder? Existing { get; private set; }
    public IReadOnlyList<(int Id, string Name)> Contacts { get; private set; } = [];
    public DocumentListSet Lists { get; private set; } = DocumentLists.Empty;
    public bool IsQuote => Kind == QuoteOrderKinds.Quote;

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await LoadPortfolioAsync())
            return Page();
        if (!CanEdit)
            return RedirectToPage("/Orders/Index", new { kind = Kind });
        if (Id is { } id)
        {
            Existing = await orders.GetOneAsync(PortfolioId, id);
            if (Existing is null || Existing.IsConverted)
            {
                TempData["Error"] = Existing is null ? "That wasn't found in this portfolio." : "This has already become an invoice or bill, so it can't be changed.";
                return RedirectToPage("/Orders/Index", new { kind = Kind });
            }
            Kind = Existing.Kind;
            Input = QuoteOrderInput.From(Existing);
            await LoadListsAsync();
        }
        else
        {
            Kind = Kind == QuoteOrderKinds.PurchaseOrder ? Kind : QuoteOrderKinds.Quote;
            await LoadListsAsync();
            var includeTax = IsQuote && (await invoices.GetSettingsAsync(PortfolioId)).AmountsIncludeTax;
            Input = QuoteOrderInput.New(Kind, DateTime.Today, Lists.Accounts.FirstOrDefault()?.AccountId ?? 0, includeTax || !IsQuote);
        }
        AddBlankLine();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!await LoadPortfolioAsync())
            return Forbid();
        Input.Lines ??= [];
        if (Id is { } id && await orders.GetOneAsync(PortfolioId, id) is { } existing)
        {
            Existing = existing;
            Input.Kind = existing.Kind;
        }
        Kind = Input.Kind;
        await LoadListsAsync();
        var result = await RunAsync(() => orders.SaveAsync(PortfolioId, Id, Input, Contacts.Select(c => c.Id).ToHashSet(), Lists.AccountIds));
        if (!result.Ok)
        {
            ModelState.AddModelError(string.Empty, result.Error!);
            AddBlankLine();
            return Page();
        }
        TempData["Message"] = "Saved.";
        return RedirectToPage("/Orders/View", new { id = result.Id });
    }

    private async Task LoadListsAsync()
    {
        Lists = await lists.LoadAsync(PortfolioId, purchases: !IsQuote);
        Contacts = IsQuote
            ? (await invoices.GetCustomersAsync(PortfolioId)).Where(c => c.IsActive || c.CustomerId == Input.ContactId).Select(c => (c.CustomerId, c.Name)).ToList()
            : (await bills.GetSuppliersAsync(PortfolioId)).Where(s => s.IsActive || s.SupplierId == Input.ContactId).Select(s => (s.SupplierId, s.Name)).ToList();
    }

    private void AddBlankLine()
    {
        if (Input.Lines.Count == 0 || !Input.Lines[^1].IsBlank)
            Input.Lines.Add(new InvoiceLineInput { Quantity = 1, AccountId = Input.Lines.LastOrDefault()?.AccountId ?? Lists.Accounts.FirstOrDefault()?.AccountId ?? 0 });
    }
}
