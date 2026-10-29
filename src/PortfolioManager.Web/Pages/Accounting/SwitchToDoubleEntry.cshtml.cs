using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Accounting;

/// <summary>
/// Switches an old single-entry portfolio to double entry: shows what will be posted, then posts it.
/// Owners only; the proc checks that again (fnCanSharePortfolio).
/// </summary>
public class SwitchToDoubleEntryModel(CurrentPortfolio current, DoubleEntryConversionService conversion) : PageModel
{
    public PortfolioSummary? Portfolio { get; private set; }
    public DoubleEntryConversionPreview? Preview { get; private set; }

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostAsync()
    {
        await LoadAsync();
        if (Portfolio is not { CanShare: true } || Preview is null || Preview.PostsJournals)
            return RedirectToPage();

        var result = await conversion.ConvertAsync(CurrentPortfolio.UserId(User), Portfolio.PortfolioId);
        if (!result.Ok)
        {
            ModelState.AddModelError(string.Empty, result.Error!);
            return Page();
        }
        TempData["Message"] = $"\"{Portfolio.Name}\" now uses double entry. {result.Id ?? 0:N0} journals were posted to the ledger.";
        return RedirectToPage("/Accounting/TrialBalance");
    }

    private async Task LoadAsync()
    {
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is not null)
            Preview = await conversion.GetPreviewAsync(CurrentPortfolio.UserId(User), Portfolio.PortfolioId);
    }
}
