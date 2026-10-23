using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Alerts;
using PortfolioManager.Core.Email;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Account;

/// <summary>
/// The emails the signed-in login gets about the picked portfolio, and its price targets.
/// Settings belong to the login, so someone with view access can have alerts too.
/// </summary>
[AllowViewOnly]
public class AlertsModel(CurrentPortfolio current, AlertService alerts, IEmailSender email, SiteBranding site) : PageModel
{
    [BindProperty] public bool PriceTargets { get; set; }
    [BindProperty] public bool Dividends { get; set; }
    [BindProperty] public bool SyncFailures { get; set; }
    [BindProperty] public bool Unallocated { get; set; }
    [BindProperty] public bool Property { get; set; }

    [BindProperty] public string? Market { get; set; } = "ASX";
    [BindProperty] public string? Code { get; set; }
    [BindProperty] public string Direction { get; set; } = "Above";
    [BindProperty] public decimal? Target { get; set; }
    [BindProperty] public string? Note { get; set; }

    [TempData] public string? Success { get; set; }

    public PortfolioSummary? Portfolio { get; private set; }
    public IReadOnlyList<PriceAlert> PriceAlerts { get; private set; } = [];
    public bool EmailWorks => email.IsConfigured && !string.IsNullOrWhiteSpace(site.BaseUrl);
    public string? LoadError { get; private set; }

    private int LoginId => CurrentPortfolio.UserId(User);

    public async Task OnGetAsync()
    {
        if (await LoadAsync() is { } s)
            (PriceTargets, Dividends, SyncFailures, Unallocated, Property) = (s.PriceTargets, s.Dividends, s.SyncFailures, s.Unallocated, s.Property);
    }

    public async Task<IActionResult> OnPostSettingsAsync()
    {
        if (await LoadAsync() is null)
            return Page();
        await alerts.SaveSettingsAsync(LoginId, Portfolio!.PortfolioId, new AlertSettings(PriceTargets, Dividends, SyncFailures, Unallocated, Property));
        Success = "Alert settings saved.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostAddAsync()
    {
        if (await LoadAsync() is not { } s)
            return Page();
        (PriceTargets, Dividends, SyncFailures, Unallocated, Property) = (s.PriceTargets, s.Dividends, s.SyncFailures, s.Unallocated, s.Property);
        if (string.IsNullOrWhiteSpace(Code))
            ModelState.AddModelError(nameof(Code), "Enter the stock code.");
        if (Target is not > 0)
            ModelState.AddModelError(nameof(Target), "Enter a target price above zero.");
        if (!ModelState.IsValid)
            return Page();

        var result = await alerts.AddPriceAlertAsync(LoginId, Portfolio!.PortfolioId, string.IsNullOrWhiteSpace(Market) ? "ASX" : Market, Code!,
            Direction == "Below" ? "Below" : "Above", Target!.Value, Note);
        if (!result.Ok)
        {
            ModelState.AddModelError(nameof(Target), result.Error!);
            return Page();
        }
        Success = $"You'll get an email when {Code!.Trim().ToUpperInvariant()} closes {(Direction == "Below" ? "at or below" : "at or above")} {Target:0.00##}.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var result = await alerts.DeletePriceAlertAsync(LoginId, id);
        Success = result.Ok ? "Price alert deleted." : result.Error;
        return RedirectToPage();
    }

    private async Task<AlertSettings?> LoadAsync()
    {
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is null)
            return null;
        try
        {
            PriceAlerts = await alerts.GetPriceAlertsAsync(LoginId, Portfolio.PortfolioId);
            return await alerts.GetSettingsAsync(LoginId, Portfolio.PortfolioId);
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 208 or 2812)
        {
            LoadError = "Alerts need the database update 2026-10-21-08-alerts.sql. Ask whoever runs this site to apply it.";
            return null;
        }
    }
}
