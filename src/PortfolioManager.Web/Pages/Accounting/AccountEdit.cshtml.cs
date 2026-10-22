using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Accounting;

public class AccountEditModel(CurrentPortfolio current, ChartOfAccountsService chart) : PageModel
{
    [BindProperty]
    public AccountInput Input { get; set; } = new();

    public PortfolioSummary? Portfolio { get; private set; }
    public LedgerAccount? Account { get; private set; }
    public IReadOnlyList<LedgerAccount> Accounts { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(int id)
    {
        if (await LoadAsync(id) is { } refusal)
            return refusal;
        Input = AccountInput.From(Account!);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int id)
    {
        if (await LoadAsync(id) is { } refusal)
            return refusal;

        // The service checks again that the account is this portfolio's own
        var result = await chart.UpdateAsync(Portfolio!.PortfolioId, id, Input);
        if (!result.Ok)
        {
            ModelState.AddModelError(string.Empty, result.Error!);
            return Page();
        }
        TempData["Message"] = $"Saved account {Input.Number} {Input.Name.Trim()}.";
        return RedirectToPage("/Accounting/Accounts");
    }

    /// <summary>Only the selected portfolio's own accounts can be edited; system accounts are read-only.</summary>
    private async Task<IActionResult?> LoadAsync(int id)
    {
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is null)
            return Forbid();

        Accounts = await chart.GetAccountsAsync(Portfolio.PortfolioId);
        if (ChartOfAccountsService.OwnAccount(Accounts, Portfolio.PortfolioId, id) is { } refusal)
        {
            TempData["Error"] = refusal.Error;
            return RedirectToPage("/Accounting/Accounts");
        }
        Account = Accounts.First(a => a.AccountId == id);
        return null;
    }
}
