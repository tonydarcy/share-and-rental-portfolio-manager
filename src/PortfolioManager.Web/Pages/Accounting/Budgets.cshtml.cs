using Microsoft.AspNetCore.Mvc;
using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Tax;
using PortfolioManager.Web.Pages.Shared;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Accounting;

/// <summary>The portfolio's budgets: one per financial year, for the whole entity or for one tracking option.</summary>
public class BudgetsModel(CurrentPortfolio current, BudgetService budgets, TrackingService tracking, ILogger<BudgetsModel> logger)
    : BooksPageModel(current, logger)
{
    public IReadOnlyList<Budget> Budgets { get; private set; } = [];
    public TrackingSetup Tracking { get; private set; } = TrackingSetup.None;
    public int ThisYear => FinancialYear.EndYearFor(Today, Core.Regions.RegionSettings.Current.YearStart);

    public async Task OnGetAsync()
    {
        if (!await LoadPortfolioAsync())
            return;
        Budgets = await budgets.GetBudgetsAsync(PortfolioId);
        Tracking = await tracking.GetAsync(PortfolioId);
    }

    public async Task<IActionResult> OnPostAddAsync(string? name, int year, int? trackingOptionId)
    {
        if (!await LoadPortfolioAsync())
            return Forbid();
        var result = await RunAsync(() => budgets.SaveBudgetAsync(PortfolioId, 0, name, year, trackingOptionId is > 0 ? trackingOptionId : null));
        return result.Ok ? Done(result, "Budget added. Enter its amounts.", new { id = result.Id }, "/Accounting/Budget") : Done(result, "");
    }
}
