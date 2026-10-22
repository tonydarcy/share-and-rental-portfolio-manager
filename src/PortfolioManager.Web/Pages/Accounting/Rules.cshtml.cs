using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Core.SalesTax;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Accounting;

/// <summary>Allocation rules for imported bank lines (?id= edits one).</summary>
public class RulesModel(CurrentPortfolio current, AllocationRuleService rules, ChartOfAccountsService chart, SalesTaxService salesTax) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public int? Id { get; set; }

    [BindProperty]
    public AllocationRuleInput Input { get; set; } = new();

    public PortfolioSummary? Portfolio { get; private set; }
    public IReadOnlyList<AllocationRule> Rules { get; private set; } = [];
    public IReadOnlyList<AccountChoice> Accounts { get; private set; } = [];
    public IReadOnlyList<TaxCode> TaxCodes { get; private set; } = [];
    public string TaxName { get; private set; } = "Tax";

    public async Task<IActionResult> OnGetAsync(string? contains)
    {
        if (await LoadAsync() is { } refusal)
            return refusal;
        if (Id is { } id && Rules.FirstOrDefault(r => r.RuleId == id) is { } rule)
            Input = new AllocationRuleInput
            {
                RuleId = rule.RuleId,
                Contains = rule.Contains,
                Direction = rule.Direction,
                AccountId = rule.AccountId,
                Description = rule.Description,
                SortOrder = rule.SortOrder,
                TaxCodeId = rule.TaxCodeId,
            };
        else if (!string.IsNullOrWhiteSpace(contains))
            Input.Contains = contains.Trim();
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        if (await LoadAsync() is { } refusal)
            return refusal;
        var result = await rules.SaveAsync(Portfolio!.PortfolioId, Input);
        if (!result.Ok)
        {
            ModelState.AddModelError(string.Empty, result.Error!);
            return Page();
        }
        TempData["Message"] = Input.RuleId is null ? "Rule added." : "Rule saved.";
        return RedirectToPage(new { id = (int?)null });
    }

    public async Task<IActionResult> OnPostDeleteAsync(int ruleId)
    {
        if (await LoadAsync() is { } refusal)
            return refusal;
        var result = await rules.DeleteAsync(Portfolio!.PortfolioId, ruleId);
        if (result.Ok)
            TempData["Message"] = "Rule deleted.";
        else
            TempData["Error"] = result.Error;
        return RedirectToPage(new { id = (int?)null });
    }

    private async Task<IActionResult?> LoadAsync()
    {
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is null)
            return HttpMethods.IsGet(Request.Method) ? Page() : Forbid();
        Rules = await rules.GetAsync(Portfolio.PortfolioId);
        Accounts = (await chart.GetJournalAccountsAsync(Portfolio.PortfolioId))
            .Where(a => !a.DisplayName.Contains(BankImportService.SuspenseAccountName))
            .ToList();
        if (await salesTax.GetSetupAsync(Portfolio.PortfolioId) is { } setup)
        {
            TaxName = setup.TaxName;
            TaxCodes = await salesTax.GetActiveCodesAsync(Portfolio.PortfolioId);
        }
        return null;
    }
}
