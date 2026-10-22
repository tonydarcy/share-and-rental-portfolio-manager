using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Invoicing;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Invoices;

/// <summary>The picked portfolio's invoices, with what is owing and overdue.</summary>
public class IndexModel(CurrentPortfolio current, InvoiceService invoices, InvoiceExtrasService extras) : PageModel
{
    public IReadOnlyDictionary<int, InvoiceKind> Kinds { get; private set; } = new Dictionary<int, InvoiceKind>();

    /// <summary>All, Unpaid (approved, not fully paid), Overdue, Draft or Paid.</summary>
    [Microsoft.AspNetCore.Mvc.BindProperty(SupportsGet = true)]
    public string Show { get; set; } = "All";

    public static readonly IReadOnlyList<string> Filters = ["All", "Unpaid", "Overdue", "Draft", "Paid"];

    public PortfolioSummary? Portfolio { get; private set; }
    public IReadOnlyList<InvoiceSummary> Invoices { get; private set; } = [];
    public DateOnly Today { get; } = DateOnly.FromDateTime(DateTime.Today);
    public decimal Owing { get; private set; }
    public decimal Overdue { get; private set; }
    public int CustomerCount { get; private set; }

    public async Task OnGetAsync()
    {
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is null)
            return;

        var all = await invoices.GetInvoicesAsync(Portfolio.PortfolioId);
        CustomerCount = (await invoices.GetCustomersAsync(Portfolio.PortfolioId)).Count;
        Kinds = await extras.GetKindsAsync(Portfolio.PortfolioId);
        // An unapplied credit note reduces what's owing
        Owing = all.Sum(i => Kinds.ContainsKey(i.InvoiceId) ? -i.Balance : i.Balance);
        Overdue = all.Where(i => i.DueDate < Today && !Kinds.ContainsKey(i.InvoiceId)).Sum(i => i.Balance);
        Invoices = Show switch
        {
            "Unpaid" => all.Where(i => i.Balance > 0).ToList(),
            "Overdue" => all.Where(i => i.Balance > 0 && i.DueDate < Today).ToList(),
            "Draft" => all.Where(i => i.Status == InvoiceStatus.Draft).ToList(),
            "Paid" => all.Where(i => i.Status == InvoiceStatus.Approved && i.Balance <= 0).ToList(),
            _ => all,
        };
    }

    public static string BadgeClass(string displayStatus) => displayStatus switch
    {
        "Paid" => "text-bg-success",
        "Overdue" => "text-bg-danger",
        "Part paid" => "text-bg-warning",
        "Unpaid" => "text-bg-primary",
        _ => "text-bg-secondary",
    };
}
