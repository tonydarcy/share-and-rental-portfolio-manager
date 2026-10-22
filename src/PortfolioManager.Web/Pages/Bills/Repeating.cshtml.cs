using Microsoft.AspNetCore.Mvc;
using PortfolioManager.Core.Bills;
using PortfolioManager.Web.Pages.Shared;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Bills;

/// <summary>Bills that come every week, month or year (rent, subscriptions): create the ones that are due.</summary>
public class RepeatingModel(CurrentPortfolio current, BillService bills, DocumentLists lists, ILogger<RepeatingModel> logger) : BooksPageModel(current, logger)
{
    public IReadOnlyList<RepeatingBill> Repeating { get; private set; } = [];
    public int DueCount => Repeating.Sum(r => r.DueDates(Today).Count);

    public async Task OnGetAsync()
    {
        if (await LoadPortfolioAsync())
            Repeating = await bills.GetRepeatingAsync(PortfolioId);
    }

    public async Task<IActionResult> OnPostCreateAsync()
    {
        if (!await LoadPortfolioAsync())
            return Forbid();
        var set = await lists.LoadAsync(PortfolioId, purchases: true);
        var (created, errors) = await bills.CreateDueAsync(PortfolioId, Today, set.AccountIds);
        TempData["Message"] = created == 0 ? "No bills were due." : $"{created} bill{(created == 1 ? "" : "s")} created.";
        if (errors.Count > 0)
            TempData["Error"] = string.Join(" ", errors);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostActiveAsync(int id, bool active)
    {
        if (!await LoadPortfolioAsync())
            return Forbid();
        return Done(await RunAsync(() => bills.SetRepeatingActiveAsync(PortfolioId, id, active)), active ? "Started again." : "Paused.");
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        if (!await LoadPortfolioAsync())
            return Forbid();
        return Done(await RunAsync(() => bills.DeleteRepeatingAsync(PortfolioId, id)), "Repeating bill deleted. Bills already created are kept.");
    }
}
