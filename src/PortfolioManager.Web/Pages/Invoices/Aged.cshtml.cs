using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Invoicing;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Invoices;

/// <summary>Aged receivables: what each customer owes, by how long it is overdue.</summary>
public class AgedModel(CurrentPortfolio current, InvoiceService invoices, InvoiceExtrasService extras) : PageModel
{
    [BindProperty(SupportsGet = true)] public DateOnly? AsAt { get; set; }

    public PortfolioSummary? Portfolio { get; private set; }
    public IReadOnlyList<AgedBalance> Rows { get; private set; } = [];

    public async Task OnGetAsync()
    {
        AsAt ??= DateOnly.FromDateTime(DateTime.Today);
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is null)
            return;
        // Payments made after the date still count as paid: the report is of balances now, aged at the date
        Rows = Receivables.Aged(await invoices.GetInvoicesAsync(Portfolio.PortfolioId), await extras.GetKindsAsync(Portfolio.PortfolioId), AsAt.Value);
    }
}
