using Microsoft.AspNetCore.Mvc;
using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Data;
using PortfolioManager.Core.Prices;
using PortfolioManager.Core.Regions;
using PortfolioManager.Web.Pages.Shared;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Accounting;

/// <summary>
/// Bank and other accounts held in a foreign currency: mark an account's currency, then record money in and out of it
/// at a rate so the books carry both the foreign balance and its value in the portfolio's currency.
/// </summary>
public class CurrencyModel(CurrentPortfolio current, ForeignCurrencyService fx, ChartOfAccountsService chart, FxRateService rates,
    ILogger<CurrencyModel> logger) : BooksPageModel(current, logger)
{
    [BindProperty(SupportsGet = true)] public int? AccountId { get; set; }

    public IReadOnlyList<ForeignAccount> Foreign { get; private set; } = [];
    public IReadOnlyList<LedgerAccount> Choices { get; private set; } = [];
    public IReadOnlyList<LedgerAccount> Others { get; private set; } = [];
    public ForeignAccount? Selected { get; private set; }
    public IReadOnlyList<ForeignEntry> Entries { get; private set; } = [];
    public decimal? SuggestedRate { get; private set; }

    public async Task OnGetAsync()
    {
        if (!await LoadAsync())
            return;
        if (Selected is not null)
        {
            Entries = await fx.GetEntriesAsync(PortfolioId, Selected.AccountId);
            SuggestedRate = (await rates.GetAsync(Selected.Currency, RegionSettings.Current.Currency, Today))?.Rate;
        }
    }

    public async Task<IActionResult> OnPostSetAsync(int accountId, string? currency)
    {
        if (!await LoadAsync())
            return Forbid();
        if (!string.IsNullOrWhiteSpace(currency) && currency.Trim().Equals(RegionSettings.Current.Currency, StringComparison.OrdinalIgnoreCase))
            return Done(ProcResult.Failed($"{currency.ToUpperInvariant()} is the portfolio's own currency."), "");
        return Done(await RunAsync(() => fx.SetCurrencyAsync(PortfolioId, accountId, currency)),
            string.IsNullOrWhiteSpace(currency) ? "The account is back in the portfolio's currency." : "Account currency set.",
            string.IsNullOrWhiteSpace(currency) ? null : new { accountId });
    }

    public async Task<IActionResult> OnPostAddAsync(DateOnly date, decimal amount, string direction, decimal rate, int otherAccountId, string? description)
    {
        if (!await LoadAsync() || Selected is null)
            return Forbid();
        var signed = direction == "out" ? -Math.Abs(amount) : Math.Abs(amount);
        return Done(await RunAsync(() => fx.AddEntryAsync(PortfolioId, Selected.AccountId, date, signed, rate, otherAccountId, description)),
            "Entry posted.", new { accountId = AccountId });
    }

    public async Task<IActionResult> OnPostDeleteAsync(int journalId)
    {
        if (!await LoadAsync())
            return Forbid();
        return Done(await RunAsync(() => fx.DeleteEntryAsync(PortfolioId, journalId)), "Entry deleted.", new { accountId = AccountId });
    }

    private async Task<bool> LoadAsync()
    {
        if (!await LoadPortfolioAsync())
            return false;
        Foreign = await fx.GetAccountsAsync(PortfolioId);
        var all = (await chart.GetAccountsAsync(PortfolioId)).Where(a => !a.IsInactive && a.Number % 1000 != 0).ToList();
        var foreignIds = Foreign.Select(f => f.AccountId).ToHashSet();
        Choices = all.Where(a => a.Type is "Asset" or "Liability" && !a.IsSystem && !foreignIds.Contains(a.AccountId)).ToList();
        Others = all.Where(a => !foreignIds.Contains(a.AccountId)).ToList();
        Selected = Foreign.FirstOrDefault(f => f.AccountId == AccountId) ?? (AccountId is null && Foreign.Count == 1 ? Foreign[0] : null);
        AccountId = Selected?.AccountId;
        return true;
    }
}
