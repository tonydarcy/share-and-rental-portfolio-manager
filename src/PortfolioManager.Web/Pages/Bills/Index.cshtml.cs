using PortfolioManager.Core.Bills;
using PortfolioManager.Core.Invoicing;
using PortfolioManager.Web.Pages.Shared;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Bills;

/// <summary>The picked portfolio's supplier bills and credits, with what is owing and overdue.</summary>
public class IndexModel(CurrentPortfolio current, BillService bills, ILogger<IndexModel> logger) : BooksPageModel(current, logger)
{
    [Microsoft.AspNetCore.Mvc.BindProperty(SupportsGet = true)]
    public string Show { get; set; } = "All";

    public static readonly IReadOnlyList<string> Filters = ["All", "Unpaid", "Overdue", "Draft", "Paid"];

    public IReadOnlyList<BillSummary> Bills { get; private set; } = [];
    public decimal Owing { get; private set; }
    public decimal Overdue { get; private set; }
    public int SupplierCount { get; private set; }

    public async Task OnGetAsync()
    {
        if (!await LoadPortfolioAsync())
            return;
        var all = await bills.GetBillsAsync(PortfolioId);
        SupplierCount = (await bills.GetSuppliersAsync(PortfolioId)).Count;
        Owing = all.Sum(b => b.IsCredit ? -b.HomeBalance : b.HomeBalance);
        Overdue = all.Where(b => !b.IsCredit && b.DueDate < Today).Sum(b => b.HomeBalance);
        Bills = Show switch
        {
            "Unpaid" => all.Where(b => b.Balance > 0).ToList(),
            "Overdue" => all.Where(b => b.Balance > 0 && b.DueDate < Today && !b.IsCredit).ToList(),
            "Draft" => all.Where(b => b.Status == InvoiceStatus.Draft).ToList(),
            "Paid" => all.Where(b => b.Status == InvoiceStatus.Approved && b.Balance <= 0).ToList(),
            _ => all,
        };
    }
}
