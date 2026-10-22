using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Clients;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Core.Statements;
using PortfolioManager.Core.Tax;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Accounting;

/// <summary>A price typed over the one on file, for a holding with no price or a different value.</summary>
public sealed class RevaluationPriceInput
{
    public string Key { get; set; } = "";
    public decimal? Price { get; set; }
}

/// <summary>Year-end journal moving investments to market value (?fy=2026).</summary>
public class RevaluationModel(CurrentPortfolio current, RevaluationService revaluations, ClientService clients, ChartOfAccountsService chart, LockedPeriodService locks) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public int? Fy { get; set; }

    [BindProperty]
    public decimal TaxRate { get; set; }

    [BindProperty]
    public List<RevaluationPriceInput> Prices { get; set; } = [];

    public int Year { get; private set; }
    public DateOnly Date => FinancialYear.EndDate(Year);
    public PortfolioSummary? Portfolio { get; private set; }
    public RevaluationPlan? Plan { get; private set; }
    public string EntityType { get; private set; } = "";
    public string Mode { get; private set; } = "";
    public bool Locked { get; private set; }

    public IReadOnlyList<int> Years
    {
        get
        {
            var years = FinancialYear.Recent(StatementsModel.Today).ToList();
            if (!years.Contains(Year))
                years.Add(Year);
            return years;
        }
    }

    public async Task<IActionResult> OnGetAsync()
    {
        if (await LoadAsync() is { } refusal)
            return refusal;
        TaxRate = RevaluationService.DefaultTaxRate(EntityType);
        Plan = await revaluations.PlanAsync(Portfolio!.PortfolioId, Date);
        return Page();
    }

    /// <summary>Recalculates with the typed prices without posting.</summary>
    public async Task<IActionResult> OnPostPreviewAsync()
    {
        if (await LoadAsync() is { } refusal)
            return refusal;
        Plan = await revaluations.PlanAsync(Portfolio!.PortfolioId, Date, TypedPrices());
        return Page();
    }

    public async Task<IActionResult> OnPostPostAsync()
    {
        if (await LoadAsync() is { } refusal)
            return refusal;
        var result = await revaluations.PostAsync(Portfolio!.PortfolioId, Date, TaxRate, TypedPrices());
        if (!result.Ok)
        {
            ModelState.AddModelError(string.Empty, result.Error!);
            Plan = await revaluations.PlanAsync(Portfolio.PortfolioId, Date, TypedPrices());
            return Page();
        }
        TempData["Message"] = $"Posted the revaluation at {Date:d MMM yyyy}: {(result.Movement >= 0 ? "unrealised gain" : "fall in value")} of {Math.Abs(result.Movement):C}"
            + (result.DeferredTax != 0 ? $", deferred tax {result.DeferredTax:C}." : ".");
        return RedirectToPage(new { fy = Year });
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        if (await LoadAsync() is { } refusal)
            return refusal;
        var result = await revaluations.DeleteAsync(Portfolio!.PortfolioId, Date);
        if (result.Ok)
            TempData["Message"] = $"Removed the revaluation at {Date:d MMM yyyy}.";
        else
            TempData["Error"] = result.Error;
        return RedirectToPage(new { fy = Year });
    }

    private Dictionary<string, decimal> TypedPrices() =>
        Prices.Where(p => p.Price is >= 0 && p.Key.Length > 0)
            .GroupBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last().Price!.Value, StringComparer.OrdinalIgnoreCase);

    private async Task<IActionResult?> LoadAsync()
    {
        Year = StatementFormat.ClampYear(Fy, FinancialYear.LastCompleted(StatementsModel.Today));
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is null)
            return HttpMethods.IsGet(Request.Method) ? Page() : Forbid();
        EntityType = (await clients.GetAsync(Portfolio.PortfolioId))?.EntityType ?? ClientEntityTypes.FromPortfolioType(Portfolio.EntityType);
        Mode = await chart.GetAccountingModeAsync(Portfolio.PortfolioId);
        Locked = (await locks.GetAsync(Portfolio.PortfolioId)).IsLocked(Date);
        return null;
    }
}
