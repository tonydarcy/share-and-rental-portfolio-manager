using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Data;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Shared;

/// <summary>
/// What the bills, orders, inventory, claims, assets and currency pages share: the picked portfolio (never one from the
/// form), and running a change so a locked period comes back as a message rather than an error page.
/// </summary>
public abstract class BooksPageModel(CurrentPortfolio current, ILogger logger) : PageModel
{
    public PortfolioSummary? Portfolio { get; private set; }
    public int PortfolioId => Portfolio!.PortfolioId;
    public bool CanEdit => Portfolio?.CanEdit == true;
    public DateOnly Today { get; } = DateOnly.FromDateTime(DateTime.Today);

    protected async Task<bool> LoadPortfolioAsync()
    {
        Portfolio = await current.GetSelectedAsync();
        return Portfolio is not null;
    }

    protected async Task<ProcResult> RunAsync(Func<Task<ProcResult>> action)
    {
        try
        {
            return await action();
        }
        catch (Exception ex) when (LockedPeriodService.IsLockError(ex))
        {
            return ProcResult.Failed(LockedPeriodFilter.Message(ex));
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is >= 50000 and < 51000)
        {
            // A proc's own THROW is a message for the person
            return ProcResult.Failed(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{Page} change failed for portfolio {PortfolioId}", GetType().Name, Portfolio?.PortfolioId);
            return ProcResult.Failed("That couldn't be saved, and nothing was changed. Try again.");
        }
    }

    /// <summary>Shows the result on the next page and redirects there.</summary>
    protected IActionResult Done(ProcResult result, string message, object? routeValues = null, string? pageName = null)
    {
        TempData[result.Ok ? "Message" : "Error"] = result.Ok ? message : result.Error;
        return RedirectToPage(pageName, routeValues);
    }
}
