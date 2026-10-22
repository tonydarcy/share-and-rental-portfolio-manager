using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Invoicing;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Invoices;

/// <summary>The people and businesses the picked portfolio invoices.</summary>
public class CustomersModel(CurrentPortfolio current, InvoiceService invoices) : PageModel
{
    /// <summary>The customer being edited, or null to add one.</summary>
    [BindProperty(SupportsGet = true)]
    public int? Id { get; set; }

    [BindProperty]
    public CustomerInput Input { get; set; } = new();

    /// <summary>Where to go after adding one, e.g. back to a new invoice.</summary>
    [BindProperty(SupportsGet = true)]
    public string? ReturnTo { get; set; }

    public PortfolioSummary? Portfolio { get; private set; }
    public IReadOnlyList<Customer> Customers { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync()
    {
        await LoadAsync();
        if (Id is { } id)
        {
            if (Customers.FirstOrDefault(c => c.CustomerId == id) is not { } customer)
                return RedirectToPage(new { id = (int?)null });
            Input = CustomerInput.From(customer);
        }
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is null)
            return Forbid();
        Input.CustomerId = Id;
        var result = await invoices.SaveCustomerAsync(Portfolio.PortfolioId, Input);
        if (!result.Ok)
        {
            ModelState.AddModelError(string.Empty, result.Error!);
            await LoadAsync();
            return Page();
        }
        TempData["Message"] = Id is null ? $"{Input.Name!.Trim()} added." : "Customer saved.";
        if (ReturnTo == "invoice" && Id is null)
            return RedirectToPage("/Invoices/Edit", new { customerId = result.Id });
        return RedirectToPage(new { id = (int?)null });
    }

    public async Task<IActionResult> OnPostDeleteAsync(int customerId)
    {
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is null)
            return Forbid();
        var result = await invoices.DeleteCustomerAsync(Portfolio.PortfolioId, customerId);
        TempData[result.Ok ? "Message" : "Error"] = result.Ok ? "Customer deleted." : result.Error;
        return RedirectToPage(new { id = (int?)null });
    }

    private async Task LoadAsync()
    {
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is not null)
            Customers = await invoices.GetCustomersAsync(Portfolio.PortfolioId);
    }
}
