using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.CorporateActions;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Core.Regions;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.CorporateActions;

public class AnnouncedModel(CurrentPortfolio current, AnnouncedActionsService announced, TimeProvider clock) : PageModel
{
    public PortfolioSummary? Portfolio { get; private set; }
    public RegionSettings Region { get; private set; } = RegionSettings.Australia;
    public AnnouncedSuggestions Suggestions { get; private set; } = new([], []);
    public string? LoadError { get; private set; }

    private DateOnly Today => DateOnly.FromDateTime(clock.GetLocalNow().DateTime);

    public async Task OnGetAsync()
    {
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is null)
            return;
        Region = await current.GetRegionAsync();
        try
        {
            Suggestions = await announced.GetAsync(Portfolio.PortfolioId, Today);
        }
        catch (Microsoft.Data.SqlClient.SqlException)
        {
            LoadError = "The announcements tables aren't in the database yet. Run Database/Migrations/2026-10-21-02-announced-actions.sql.";
        }
    }

    public string? NeedsReview(DividendSuggestion d) => AnnouncedPayouts.NeedsReview(d, Region.Currency);

    public async Task<IActionResult> OnPostConfirmDividendAsync(int id)
    {
        var portfolio = await current.GetSelectedAsync();
        if (portfolio is null)
            return Forbid();
        var region = await current.GetRegionAsync();

        // Re-checked for this portfolio: only a suggestion that is still offered and safe for one click
        var d = await announced.GetDividendAsync(portfolio.PortfolioId, id, Today);
        if (d is null)
            TempData["Message"] = "That dividend is no longer waiting (already recorded or dismissed), so nothing was added.";
        else if (AnnouncedPayouts.NeedsReview(d, region.Currency) is { } why)
            TempData["Message"] = $"{d.StockCode} wasn't added: {why}";
        else
        {
            var payout = await announced.ConfirmDividendAsync(portfolio.PortfolioId, id, Today);
            TempData["Message"] = payout is null
                ? "That dividend is no longer waiting, so nothing was added."
                : $"Added the {payout.StockCode} payout of {payout.Total:C} on {payout.Date:d MMM yyyy}.";
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostApplySplitAsync(int id)
    {
        var portfolio = await current.GetSelectedAsync();
        if (portfolio is null)
            return Forbid();

        TempData["Message"] = await announced.ApplySplitAsync(portfolio.PortfolioId, id, Today) switch
        {
            null => "That split is no longer waiting (already recorded or dismissed), so nothing was changed.",
            ({ } s, { Ok: true }) => $"Recorded the {s.StockCode} {s.Ratio.Kind.ToLowerInvariant()} ({s.Ratio}) from {s.EffectiveDate:d MMM yyyy}.",
            (_, { Error: var error }) => $"The split wasn't recorded: {error}",
        };
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDismissAsync(string kind, int id)
    {
        var portfolio = await current.GetSelectedAsync();
        if (portfolio is null)
            return Forbid();
        if (kind is not ("Dividend" or "Split"))
            return BadRequest();

        await announced.DismissAsync(portfolio.PortfolioId, kind, id);
        TempData["Message"] = $"{kind} dismissed. It won't be offered again for this portfolio.";
        return RedirectToPage();
    }
}
