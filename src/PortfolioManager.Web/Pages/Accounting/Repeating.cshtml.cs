using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Accounting;

/// <summary>Repeating journals: what's due, posting it, pausing, changing and deleting the schedules.</summary>
public class RepeatingModel(CurrentPortfolio current, RecurringJournalService repeating, ChartOfAccountsService chart) : PageModel
{
    [TempData] public string? Message { get; set; }
    [TempData] public string? Error { get; set; }

    public PortfolioSummary? Portfolio { get; private set; }
    public IReadOnlyList<RecurringJournal> Journals { get; private set; } = [];
    public IReadOnlyDictionary<int, string> AccountNames { get; private set; } = new Dictionary<int, string>();
    public DateOnly Today { get; } = DateOnly.FromDateTime(DateTime.Today);

    public async Task OnGetAsync()
    {
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is null)
            return;
        Journals = await repeating.ListAsync(Portfolio.PortfolioId);
        AccountNames = (await chart.GetJournalAccountsAsync(Portfolio.PortfolioId)).ToDictionary(a => a.AccountId, a => a.DisplayName);
    }

    public async Task<IActionResult> OnPostPostDueAsync()
    {
        if (await current.GetSelectedAsync() is not { } p)
            return Forbid();
        var result = await repeating.PostDueAsync(p.PortfolioId, Today);
        Message = result.Posted == 0 && result.Errors.Count == 0 ? "Nothing is due." : $"Posted {result.Posted} journal{(result.Posted == 1 ? "" : "s")}.";
        if (result.Errors.Count > 0)
            Error = string.Join(" ", result.Errors);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostActiveAsync(int id, bool active)
    {
        if (await current.GetSelectedAsync() is not { } p)
            return Forbid();
        var r = await repeating.SetActiveAsync(p.PortfolioId, id, active);
        if (r.Ok) Message = active ? "Started again." : "Paused."; else Error = r.Error;
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        if (await current.GetSelectedAsync() is not { } p)
            return Forbid();
        var r = await repeating.DeleteAsync(p.PortfolioId, id);
        if (r.Ok) Message = "Deleted. Journals already posted stay in the ledger."; else Error = r.Error;
        return RedirectToPage();
    }
}
