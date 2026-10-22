using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Invoicing;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Invoices;

/// <summary>Repeating invoices (monthly rent or fees): what's due, creating them, pausing and deleting.</summary>
public class RepeatingModel(CurrentPortfolio current, InvoiceExtrasService extras, ChartOfAccountsService chart) : PageModel
{
    public PortfolioSummary? Portfolio { get; private set; }
    public IReadOnlyList<RepeatingInvoice> Items { get; private set; } = [];
    public DateOnly Today { get; } = DateOnly.FromDateTime(DateTime.Today);

    public async Task OnGetAsync()
    {
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is not null)
            Items = await extras.GetRepeatingAsync(Portfolio.PortfolioId);
    }

    public async Task<IActionResult> OnPostCreateDueAsync()
    {
        if (await current.GetSelectedAsync() is not { } p)
            return Forbid();
        var income = (await chart.GetAccountsAsync(p.PortfolioId)).Where(a => a.Type == "Revenue").Select(a => a.AccountId).ToHashSet();
        var (created, errors) = await extras.CreateDueAsync(p.PortfolioId, Today, income);
        TempData["Message"] = created == 0 && errors.Count == 0 ? "Nothing is due." : $"Created {created} invoice{(created == 1 ? "" : "s")}.";
        if (errors.Count > 0)
            TempData["Error"] = string.Join(" ", errors);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostActiveAsync(int id, bool active)
    {
        if (await current.GetSelectedAsync() is not { } p)
            return Forbid();
        var r = await extras.SetRepeatingActiveAsync(p.PortfolioId, id, active);
        TempData[r.Ok ? "Message" : "Error"] = r.Ok ? (active ? "Started again." : "Paused.") : r.Error;
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        if (await current.GetSelectedAsync() is not { } p)
            return Forbid();
        var r = await extras.DeleteRepeatingAsync(p.PortfolioId, id);
        TempData[r.Ok ? "Message" : "Error"] = r.Ok ? "Deleted. Invoices already made stay." : r.Error;
        return RedirectToPage();
    }
}
