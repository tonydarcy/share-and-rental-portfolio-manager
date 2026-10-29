using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Portfolio;

/// <summary>
/// Permanently deletes part of the picked portfolio, or all of it. Owners only; the procs check that again
/// (fnCanDeletePortfolio) and the portfolio's name must be typed to confirm.
/// </summary>
public class DeleteModel(CurrentPortfolio current, DeletionService deletion, PortfolioService portfolios) : PageModel
{
    /// <summary>A <see cref="DataPart"/> name.</summary>
    [BindProperty]
    public string? Part { get; set; }

    [BindProperty]
    public string? ConfirmName { get; set; }

    public PortfolioSummary? Portfolio { get; private set; }
    public PortfolioDataCounts? Counts { get; private set; }
    public bool IsLoginPortfolio { get; private set; }
    public IReadOnlyList<PortfolioShare> Shares { get; private set; } = [];
    public int UserId { get; private set; }

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostDataAsync()
    {
        await LoadAsync();
        if (Portfolio is not { CanShare: true })
            return RedirectToPage();

        var part = DeletionService.ParsePart(Part);
        if (part is null)
            ModelState.AddModelError(nameof(Part), "Choose what to delete.");
        else if (!DeletionService.Confirms(ConfirmName, Portfolio.Name))
            ModelState.AddModelError(nameof(ConfirmName), $"Type the portfolio's name, {Portfolio.Name}, to confirm.");
        if (!ModelState.IsValid)
            return Page();

        var result = await deletion.DeleteDataAsync(UserId, Portfolio.PortfolioId, part!.Value);
        if (!result.Ok)
        {
            ModelState.AddModelError(string.Empty, result.Error!);
            return Page();
        }
        TempData["Message"] = $"Deleted: {DeletionService.Title(part.Value).ToLowerInvariant()} in \"{Portfolio.Name}\".";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostPortfolioAsync()
    {
        await LoadAsync();
        if (Portfolio is not { CanShare: true } || IsLoginPortfolio)
            return RedirectToPage();

        if (!DeletionService.Confirms(ConfirmName, Portfolio.Name))
        {
            ModelState.AddModelError("ConfirmPortfolio", $"Type the portfolio's name, {Portfolio.Name}, to confirm.");
            return Page();
        }

        // The proc compares the name itself, so send the stored name once the typed one matches it
        var result = await deletion.DeletePortfolioAsync(UserId, Portfolio.PortfolioId, Portfolio.Name);
        if (!result.Ok)
        {
            ModelState.AddModelError("ConfirmPortfolio", result.Error!);
            return Page();
        }
        current.Refresh();
        TempData["Message"] = $"The portfolio \"{Portfolio.Name}\" and everything in it has been deleted.";
        return RedirectToPage("/Portfolio/Manage");
    }

    private async Task LoadAsync()
    {
        UserId = CurrentPortfolio.UserId(User);
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is not { CanShare: true })
            return;
        Counts = await deletion.GetCountsAsync(Portfolio.PortfolioId);
        IsLoginPortfolio = await deletion.IsLoginPortfolioAsync(Portfolio.PortfolioId);
        Shares = (await portfolios.GetSharesAsync(UserId, Portfolio.PortfolioId)).Where(s => s.UserId != UserId).ToList();
    }
}
