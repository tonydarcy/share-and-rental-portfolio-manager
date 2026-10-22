using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Accounting;

public class AccountsModel(CurrentPortfolio current, ChartOfAccountsService chart) : PageModel
{
    [BindProperty]
    public AccountInput Input { get; set; } = new();

    public PortfolioSummary? Portfolio { get; private set; }
    public string Mode { get; private set; } = "";
    public IReadOnlyList<LedgerAccount> Accounts { get; private set; } = [];

    /// <summary>Opens the add form after a failed add, so the errors are in view.</summary>
    public bool ShowAddForm { get; private set; }

    public async Task OnGetAsync()
    {
        await LoadAsync();
    }

    public async Task<IActionResult> OnPostAddAsync()
    {
        if (await LoadAsync() is null)
            return Forbid();

        var result = await chart.AddAsync(Portfolio!.PortfolioId, Input);
        if (!result.Ok)
        {
            ModelState.AddModelError(string.Empty, result.Error!);
            ShowAddForm = true;
            return Page();
        }
        TempData["Message"] = $"Added account {Input.Number} {Input.Name.Trim()}.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostActiveAsync(int id, bool active)
    {
        var portfolio = await current.GetSelectedAsync();
        if (portfolio is null)
            return Forbid();

        // The service only changes an account that belongs to the selected portfolio
        var result = await chart.SetActiveAsync(portfolio.PortfolioId, id, active);
        TempData[result.Ok ? "Message" : "Error"] = result.Ok ? (active ? "Account reactivated." : "Account deactivated.") : result.Error;
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var portfolio = await current.GetSelectedAsync();
        if (portfolio is null)
            return Forbid();

        var result = await chart.DeleteAsync(portfolio.PortfolioId, id);
        TempData[result.Ok ? "Message" : "Error"] = result.Ok ? "Account deleted." : result.Error;
        return RedirectToPage();
    }

    private async Task<PortfolioSummary?> LoadAsync()
    {
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is null)
            return null;
        Accounts = await chart.GetAccountsAsync(Portfolio.PortfolioId);
        Mode = await chart.GetAccountingModeAsync(Portfolio.PortfolioId);
        return Portfolio;
    }
}
