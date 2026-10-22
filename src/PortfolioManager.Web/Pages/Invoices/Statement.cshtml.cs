using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Clients;
using PortfolioManager.Core.Invoicing;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Invoices;

/// <summary>A customer statement: the opening balance, invoices, credit notes and payments in the period, and what's overdue.</summary>
public class StatementModel(CurrentPortfolio current, InvoiceService invoices, InvoiceExtrasService extras, ClientService clients) : PageModel
{
    [BindProperty(SupportsGet = true)] public int? Customer { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? From { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? To { get; set; }

    public PortfolioSummary? Portfolio { get; private set; }
    public IReadOnlyList<Customer> Customers { get; private set; } = [];
    public CustomerStatement? Statement { get; private set; }
    public ClientDetails? Business { get; private set; }
    public bool HasLogo { get; private set; }

    public async Task OnGetAsync()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        To ??= today;
        From ??= new DateOnly(To.Value.Year, To.Value.Month, 1).AddMonths(-2);
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is null)
            return;
        var id = Portfolio.PortfolioId;
        Customers = await invoices.GetCustomersAsync(id);
        if (Customers.FirstOrDefault(c => c.CustomerId == Customer) is not { } customer)
            return;
        var kinds = await extras.GetKindsAsync(id);
        var all = (await invoices.GetInvoicesAsync(id)).Where(i => i.CustomerId == customer.CustomerId && i.Status == InvoiceStatus.Approved).ToList();
        var details = new List<InvoiceDetail>();
        foreach (var i in all)
            if (await invoices.GetInvoiceAsync(id, i.InvoiceId) is { } d)
                details.Add(d);
        var (opening, lines) = Receivables.Statement(Receivables.Movements(details, kinds), From.Value, To.Value);
        Statement = new CustomerStatement(customer, From.Value, To.Value, opening, lines, Receivables.Aged(all, kinds, To.Value));
        Business = await clients.GetAsync(id);
        HasLogo = await extras.GetLogoAsync(id) is not null;
    }
}
