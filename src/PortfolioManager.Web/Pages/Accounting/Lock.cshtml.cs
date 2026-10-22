using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Core.Tax;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Accounting;

/// <summary>Locks financial years once the statements are signed.</summary>
public class LockModel(CurrentPortfolio current, LockedPeriodService locks) : PageModel
{
    public PortfolioSummary? Portfolio { get; private set; }
    public LockedPeriod Lock { get; private set; } = LockedPeriod.None;
    public IReadOnlyList<LockChange> History { get; private set; } = [];

    /// <summary>Completed years, newest first, plus any locked year older than those.</summary>
    public IReadOnlyList<int> Years { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync()
    {
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is null)
            return Page();
        Lock = await locks.GetAsync(Portfolio.PortfolioId);
        History = await locks.GetHistoryAsync(Portfolio.PortfolioId);
        var years = FinancialYear.Recent(StatementsModel.Today).Skip(1).ToList();
        if (Lock.LockedTo is { } to && !years.Contains(FinancialYear.EndYearFor(to)))
            years.Add(FinancialYear.EndYearFor(to));
        Years = years;
        return Page();
    }

    public async Task<IActionResult> OnPostLockAsync(int fy) =>
        await SetAsync(FinancialYear.EndDate(fy), $"Locked everything up to the end of {FinancialYear.Label(fy)}.");

    public async Task<IActionResult> OnPostUnlockAsync(int fy) =>
        await SetAsync(LockedPeriod.UnlockTo(fy), $"Unlocked {FinancialYear.Label(fy)}. Earlier years stay locked.");

    public async Task<IActionResult> OnPostRemoveAsync() =>
        await SetAsync(null, "Removed the lock. Every year can be changed again.");

    private async Task<IActionResult> SetAsync(DateOnly? lockedTo, string message)
    {
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is null)
            return Forbid();
        var result = await locks.SetAsync(Portfolio.PortfolioId, lockedTo, CurrentPortfolio.UserId(User));
        if (result.Ok)
            TempData["Message"] = message;
        else
            TempData["Error"] = result.Error;
        return RedirectToPage();
    }
}
