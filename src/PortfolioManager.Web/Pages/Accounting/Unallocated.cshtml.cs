using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Accounting;

public class UnallocatedModel(CurrentPortfolio current, BankImportService imports, AllocationRuleService rules) : PageModel
{
    public PortfolioSummary? Portfolio { get; private set; }
    public IReadOnlyList<UnallocatedLine> Lines { get; private set; } = [];

    /// <summary>The rule that would allocate each line, by line id.</summary>
    public IReadOnlyDictionary<int, AllocationRule> Suggested { get; private set; } = new Dictionary<int, AllocationRule>();

    public async Task OnGetAsync()
    {
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is null)
            return;
        Lines = await imports.GetUnallocatedAsync(Portfolio.PortfolioId);
        Suggested = await rules.SuggestAsync(Portfolio.PortfolioId, Lines);
    }

    public async Task<IActionResult> OnPostApplyAsync()
    {
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is null)
            return Forbid();
        var applied = await rules.ApplyAsync(Portfolio.PortfolioId);
        TempData["Message"] = RulesMessage(applied);
        if (applied.Errors.Count > 0)
            TempData["Error"] = string.Join(" ", applied.Errors);
        return RedirectToPage();
    }

    public static string RulesMessage(RulesApplied applied) =>
        $"Allocation rules allocated {applied.Allocated} transaction{(applied.Allocated == 1 ? "" : "s")}"
        + (applied.Skipped > 0 ? $"; {applied.Skipped} matching a rule couldn't be allocated (in a locked year, or already allocated)." : ".");
}
