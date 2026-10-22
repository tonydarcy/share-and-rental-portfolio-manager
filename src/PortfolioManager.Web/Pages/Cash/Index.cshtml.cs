using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Cash;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Cash;

public class IndexModel(CurrentPortfolio current, CashService cash) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? Account { get; set; }

    [BindProperty(SupportsGet = true)]
    public bool OldestFirst { get; set; }

    public PortfolioSummary? Portfolio { get; private set; }
    public IReadOnlyList<string> Accounts { get; private set; } = [];
    public IReadOnlyList<CashTransaction> Transactions { get; private set; } = [];

    public async Task OnGetAsync()
    {
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is null)
            return;

        Accounts = await cash.GetAccountsAsync(Portfolio.PortfolioId);
        // Only filter by an account this portfolio actually has
        Account = Accounts.FirstOrDefault(a => a == Account);

        Transactions = await cash.GetTransactionsAsync(Portfolio.PortfolioId, Account, OldestFirst);
    }
}
