using Microsoft.AspNetCore.Mvc;
using PortfolioManager.Core.Clients;
using PortfolioManager.Core.Orders;
using PortfolioManager.Web.Pages.Shared;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Orders;

/// <summary>A quote or purchase order as it is sent (print or save as PDF), with its status and the step to an invoice or bill.</summary>
public class ViewModel(CurrentPortfolio current, QuoteOrderService orders, ClientService clients, DocumentLists lists, ILogger<ViewModel> logger)
    : BooksPageModel(current, logger)
{
    [BindProperty(SupportsGet = true)]
    public int Id { get; set; }

    public QuoteOrder? Document { get; private set; }
    public ClientDetails? Business { get; private set; }
    public DocumentListSet Lists { get; private set; } = DocumentLists.Empty;
    public string BusinessName => Business?.LegalName is { Length: > 0 } n ? n : Portfolio?.Name ?? "";

    public async Task<IActionResult> OnGetAsync() => await LoadAsync() ? Page() : RedirectToPage("/Orders/Index");

    public async Task<IActionResult> OnPostStatusAsync(string status)
    {
        if (!await LoadAsync())
            return RedirectToPage("/Orders/Index");
        return Done(await RunAsync(() => orders.SetStatusAsync(PortfolioId, Id, status)), $"Marked as {status.ToLowerInvariant()}.", new { id = Id });
    }

    public async Task<IActionResult> OnPostInvoiceAsync()
    {
        if (!await LoadAsync())
            return RedirectToPage("/Orders/Index");
        var result = await RunAsync(() => orders.ToInvoiceAsync(PortfolioId, Document!, DateTime.Today, Lists.AccountIds));
        if (!result.Ok)
            return Done(result, "", new { id = Id });
        TempData["Message"] = "Draft invoice made from the quote. Check it, then approve it.";
        return RedirectToPage("/Invoices/Edit", new { id = result.Id });
    }

    public async Task<IActionResult> OnPostBillAsync(DateTime? billDate, string? supplierNumber)
    {
        if (!await LoadAsync())
            return RedirectToPage("/Orders/Index");
        var result = await RunAsync(() => orders.ToBillAsync(PortfolioId, Document!, billDate ?? DateTime.Today, supplierNumber, Lists.AccountIds));
        if (!result.Ok)
            return Done(result, "", new { id = Id });
        TempData["Message"] = "Draft bill made from the purchase order. Change anything that differs on the supplier's invoice, then approve it.";
        return RedirectToPage("/Bills/Edit", new { id = result.Id });
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        if (!await LoadAsync())
            return RedirectToPage("/Orders/Index");
        var kind = Document!.Kind;
        var result = await RunAsync(() => orders.DeleteAsync(PortfolioId, Id));
        return result.Ok ? Done(result, "Deleted.", new { kind }, "/Orders/Index") : Done(result, "", new { id = Id });
    }

    private async Task<bool> LoadAsync()
    {
        if (!await LoadPortfolioAsync())
            return false;
        Document = await orders.GetOneAsync(PortfolioId, Id);
        if (Document is null)
        {
            TempData["Error"] = "That wasn't found in this portfolio.";
            return false;
        }
        Business = await clients.GetAsync(PortfolioId);
        Lists = await lists.LoadAsync(PortfolioId, purchases: !Document.IsQuote);
        return true;
    }
}
