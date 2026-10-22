using Microsoft.AspNetCore.Mvc;
using PortfolioManager.Core.Bills;
using PortfolioManager.Web.Pages.Shared;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Bills;

/// <summary>What is owed to each supplier as at a date, by how overdue it is.</summary>
public class AgedModel(CurrentPortfolio current, BillService bills, ILogger<AgedModel> logger) : BooksPageModel(current, logger)
{
    [BindProperty(SupportsGet = true)]
    public DateTime? AsAt { get; set; }

    public IReadOnlyList<AgedPayable> Rows { get; private set; } = [];

    public async Task OnGetAsync()
    {
        if (!await LoadPortfolioAsync())
            return;
        AsAt ??= DateTime.Today;
        Rows = Payables.Aged(await bills.GetBillsAsync(PortfolioId), DateOnly.FromDateTime(AsAt.Value));
    }
}
