using Microsoft.AspNetCore.Mvc;
using PortfolioManager.Core.Accounting;
using PortfolioManager.Web.Pages.Shared;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Accounting;

/// <summary>One budget: the amount for each income and expense account each month, and budget against actual.</summary>
public class BudgetModel(CurrentPortfolio current, BudgetService budgets, TrackingService tracking, ChartOfAccountsService chart,
    ILogger<BudgetModel> logger) : BooksPageModel(current, logger)
{
    [BindProperty(SupportsGet = true)] public int Id { get; set; }
    [BindProperty(SupportsGet = true)] public string? View { get; set; }

    /// <summary>Posted as Amounts[accountId][month 0-11]; Yearly[accountId] spreads a year's figure evenly.</summary>
    [BindProperty] public Dictionary<int, decimal?[]> Amounts { get; set; } = [];
    [BindProperty] public Dictionary<int, decimal?> Yearly { get; set; } = [];

    public Budget? Budget { get; private set; }
    public BudgetAmounts Current { get; private set; } = new(new Dictionary<int, decimal[]>());
    public IReadOnlyList<LedgerAccount> Accounts { get; private set; } = [];
    public IReadOnlyList<DateOnly> Months { get; private set; } = [];
    public IReadOnlyList<BudgetLine> Compare { get; private set; } = [];
    public TrackingSetup Tracking { get; private set; } = TrackingSetup.None;
    public bool Comparing => View == "compare";

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await LoadAsync())
            return RedirectToPage("/Accounting/Budgets");
        if (Comparing)
            Compare = await budgets.CompareAsync(PortfolioId, Budget!, Core.Regions.RegionSettings.Current.YearStart);
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        if (!await LoadAsync())
            return Forbid();
        var amounts = new Dictionary<int, decimal[]>();
        foreach (var a in Accounts)
        {
            var months = Yearly.GetValueOrDefault(a.AccountId) is { } year
                ? BudgetRules.Spread(year)
                : (Amounts.GetValueOrDefault(a.AccountId) ?? []).Select(v => v ?? 0).Concat(Enumerable.Repeat(0m, 12)).Take(12).ToArray();
            if (months.Any(m => m != 0))
                amounts[a.AccountId] = months;
        }
        return Done(await RunAsync(() => budgets.SaveAmountsAsync(PortfolioId, Id, amounts)), "Budget saved.", new { id = Id });
    }

    public async Task<IActionResult> OnPostCopyActualsAsync(int fromYear)
    {
        if (!await LoadAsync())
            return Forbid();
        // Last year's actual amounts as this budget's starting point
        var last = new Core.Accounting.Budget(0, "", fromYear, Budget!.TrackingOptionId, DateTime.Now);
        var actual = await budgets.CompareAsync(PortfolioId, last, Core.Regions.RegionSettings.Current.YearStart);
        var amounts = actual.Where(l => l.ActualTotal != 0).ToDictionary(l => l.AccountId, l => l.Actual.Select(v => Math.Round(v, 0)).ToArray());
        return Done(await RunAsync(() => budgets.SaveAmountsAsync(PortfolioId, Id, amounts)), "Last year's actual amounts copied in. Change them as needed.", new { id = Id });
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        if (!await LoadAsync())
            return Forbid();
        var result = await RunAsync(() => budgets.DeleteAsync(PortfolioId, Id));
        return result.Ok ? Done(result, "Budget deleted.", null, "/Accounting/Budgets") : Done(result, "", new { id = Id });
    }

    private async Task<bool> LoadAsync()
    {
        if (!await LoadPortfolioAsync())
            return false;
        Budget = (await budgets.GetBudgetsAsync(PortfolioId)).FirstOrDefault(b => b.BudgetId == Id);
        if (Budget is null)
        {
            TempData["Error"] = "That budget wasn't found in this portfolio.";
            return false;
        }
        Current = await budgets.GetAmountsAsync(PortfolioId, Id);
        Months = BudgetRules.Months(Budget.Year, Core.Regions.RegionSettings.Current.YearStart);
        Tracking = await tracking.GetAsync(PortfolioId);
        Accounts = (await chart.GetAccountsAsync(PortfolioId))
            .Where(a => a.Type is "Revenue" or "Expense" && a.Number % 1000 != 0 && (!a.IsInactive || Current.ByAccount.ContainsKey(a.AccountId)))
            .OrderBy(a => a.Type == "Revenue" ? 0 : 1).ThenBy(a => a.Number).ToList();
        return true;
    }
}
