using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Cash;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Core.Validation;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Cash;

public class AddModel(CurrentPortfolio current, CashService cash) : PageModel
{
    [BindProperty]
    public CashForm Input { get; set; } = new();

    [TempData]
    public string? Success { get; set; }

    public PortfolioSummary? Portfolio { get; private set; }
    public string PortfolioCurrency { get; private set; } = "AUD";
    public IReadOnlyList<BankAccount> BankAccounts { get; private set; } = [];

    public async Task OnGetAsync()
    {
        if (!await LoadAsync())
            return;
        Input = new CashForm { Date = DateOnly.FromDateTime(DateTime.Now), Direction = "Deposit" };
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!await LoadAsync())
            return Page();

        var result = CashEntry.Validate(Input, FormRules.LatestToday(DateTime.UtcNow));
        foreach (var e in result.Errors)
        {
            // Keep the binder's own message (e.g. "not a number") rather than adding a second one
            var key = $"{nameof(Input)}.{e.Field}";
            if (ModelState[key] is not { Errors.Count: > 0 })
                ModelState.AddModelError(key, e.Message);
        }
        if (!ModelState.IsValid || result.Value is not { } entry)
            return Page();

        entry = entry with { Currency = CashEntry.ResolveCurrency(entry.Currency, entry.BankAccount, BankAccounts) };
        await cash.AddAsync(Portfolio!.PortfolioId, entry);

        var currency = entry.Currency ?? PortfolioCurrency;
        Success = $"{entry.Direction} of {entry.Amount:N2} {currency} on {entry.Date:d} recorded"
            + (entry.BankAccount is null ? "." : $" in {entry.BankAccount}.");
        return RedirectToPage();
    }

    private async Task<bool> LoadAsync()
    {
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is null)
            return false;
        PortfolioCurrency = await cash.GetPortfolioCurrencyAsync(Portfolio.PortfolioId);
        BankAccounts = await cash.GetBankAccountsAsync(Portfolio.PortfolioId);
        return true;
    }
}
