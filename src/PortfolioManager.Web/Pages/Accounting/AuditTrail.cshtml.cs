using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Core.Practice;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Accounting;

/// <summary>Who changed what and when in the picked portfolio, with the changes to a year after it was locked picked out.</summary>
public class AuditTrailModel(CurrentPortfolio current, AuditTrailService audit, LockedPeriodService locks) : PageModel
{
    [BindProperty(SupportsGet = true)] public DateOnly? From { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? To { get; set; }
    [BindProperty(SupportsGet = true)] public string? Table { get; set; }
    [BindProperty(SupportsGet = true)] public bool AfterLock { get; set; }

    public PortfolioSummary? Portfolio { get; private set; }
    public IReadOnlyList<AuditEntry> Entries { get; private set; } = [];
    public IReadOnlyList<LockChange> LockHistory { get; private set; } = [];

    public async Task OnGetAsync()
    {
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is null)
            return;
        var today = DateOnly.FromDateTime(DateTime.Today);
        To ??= today;
        From ??= today.AddMonths(-3);
        if (Table is not null && !AuditRules.Tables.Contains(Table))
            Table = null;
        Entries = await audit.ListAsync(Portfolio.PortfolioId, From.Value, To.Value, Table, AfterLock);
        LockHistory = await locks.GetHistoryAsync(Portfolio.PortfolioId);
    }
}
