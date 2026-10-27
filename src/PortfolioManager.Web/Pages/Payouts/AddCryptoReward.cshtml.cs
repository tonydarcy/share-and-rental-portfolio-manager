using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Cash;
using PortfolioManager.Core.Crypto;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Core.Validation;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Payouts;

/// <summary>Coins received as income (staking, an airdrop, mining): other income paid as units at their market value.</summary>
public class AddCryptoRewardModel(CurrentPortfolio current, CryptoService crypto, CashService cash) : PageModel
{
    [BindProperty]
    public RewardForm Input { get; set; } = new();

    public PortfolioSummary? Portfolio { get; private set; }
    public string PortfolioCurrency { get; private set; } = "AUD";

    public async Task OnGetAsync()
    {
        if (await LoadAsync())
            Input = new RewardForm { Date = DateOnly.FromDateTime(DateTime.Now) };
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!await LoadAsync())
            return Forbid();

        var result = CryptoEntry.Validate(Input, PortfolioCurrency, FormRules.LatestToday(DateTime.UtcNow));
        foreach (var e in result.Errors)
        {
            var key = $"{nameof(Input)}.{e.Field}";
            if (ModelState[key] is not { Errors.Count: > 0 })
                ModelState.AddModelError(key, e.Message);
        }
        if (!ModelState.IsValid || result.Value is not { } reward)
            return Page();

        var saved = await crypto.AddRewardAsync(Portfolio!.PortfolioId, reward);
        if (!saved.Ok)
        {
            ModelState.AddModelError(string.Empty, saved.Error!);
            return Page();
        }
        TempData["Message"] = $"Added the {RewardKinds.Label(reward.Kind).ToLowerInvariant()}: {reward.Units:#,##0.########} {reward.Code} worth {reward.Value:C}, " +
            "counted as income and as the cost of the new coins.";
        return RedirectToPage("/Payouts/Index");
    }

    private async Task<bool> LoadAsync()
    {
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is null)
            return false;
        PortfolioCurrency = await cash.GetPortfolioCurrencyAsync(Portfolio.PortfolioId);
        return true;
    }
}
