using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Core.Regions;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Portfolio;

/// <summary>
/// The picked portfolio's country, financial year start, currency and date/number format.
/// Australia is the default; only Australia gets the franking and CGT discount wording.
/// </summary>
public class SettingsModel(CurrentPortfolio current, PortfolioService portfolios, ChartOfAccountsService chart) : PageModel
{
    [BindProperty]
    public string CountryCode { get; set; } = RegionSettings.AustraliaCode;

    [BindProperty]
    public int YearStartMonth { get; set; } = 7;

    [BindProperty]
    public int YearStartDay { get; set; } = 1;

    [BindProperty]
    public string Currency { get; set; } = "AUD";

    [BindProperty]
    public string Format { get; set; } = "en-AU";

    public PortfolioSummary? Portfolio { get; private set; }

    /// <summary>The currency when the page was opened, to warn that changing it doesn't convert anything.</summary>
    public string SavedCurrency { get; private set; } = "AUD";

    /// <summary>The portfolio's strAccountingMode; an old single-entry one is offered the switch to double entry.</summary>
    public string AccountingMode { get; private set; } = "DoubleEntry";

    /// <summary>The menu choices posted from the Menu card, by area name (Automatic, Show or Hide).</summary>
    [BindProperty]
    public Dictionary<string, MenuChoice> Menu { get; set; } = [];

    /// <summary>What the menu shows now and why, for the Menu card.</summary>
    public MenuUsage? MenuUsage { get; private set; }

    public async Task OnGetAsync()
    {
        Portfolio = await current.GetSelectedAsync();
        MenuUsage = await current.GetMenuUsageAsync();
        var region = await current.GetRegionAsync();
        CountryCode = region.CountryCode;
        YearStartMonth = region.YearStart.Month;
        YearStartDay = region.YearStart.Day;
        Currency = region.Currency;
        Format = region.Format;
        SavedCurrency = region.Currency;
        if (Portfolio is not null)
            AccountingMode = await chart.GetAccountingModeAsync(Portfolio.PortfolioId);
    }

    /// <summary>Saves the Menu card: the portfolio's areas (needs edit access) and the login's own practice view.</summary>
    public async Task<IActionResult> OnPostMenuAsync()
    {
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is null)
            return RedirectToPage();
        var userId = CurrentPortfolio.UserId(User);
        var accessible = await current.GetAccessibleAsync();
        var region = await current.GetRegionAsync();
        var usage = await portfolios.GetMenuUsageAsync(userId, Portfolio.PortfolioId, accessible.Count, region.IsAustralia);

        var posted = Menu
            .Select(m => (Ok: Enum.TryParse<MenuArea>(m.Key, out var area), Area: area, Choice: m.Value))
            .Where(m => m.Ok)
            .ToDictionary(m => m.Area, m => m.Choice);
        var portfolioChanges = posted.Where(p => MenuAreas.PortfolioAreas.Contains(p.Key)).ToDictionary();
        var loginText = usage.LoginChoices;

        if (portfolioChanges.Count > 0 && Portfolio.CanEdit)
        {
            var text = MenuAreas.Merge(usage.PortfolioChoices, portfolioChanges);
            var result = await portfolios.SaveMenuAreasAsync(userId, Portfolio.PortfolioId, text);
            if (!result.Ok)
            {
                TempData["Error"] = result.Error;
                return RedirectToPage();
            }
            if (Portfolio.PortfolioId == userId)
                loginText = text; // the login's own portfolio keeps both on one row
        }
        if (posted.TryGetValue(MenuArea.Practice, out var practice))
        {
            var result = await portfolios.SaveMenuAreasAsync(userId, userId,
                MenuAreas.Merge(loginText, new Dictionary<MenuArea, MenuChoice> { [MenuArea.Practice] = practice }));
            if (!result.Ok)
            {
                TempData["Error"] = result.Error;
                return RedirectToPage();
            }
        }

        foreach (var p in accessible)
            current.ForgetMenu(p.PortfolioId);
        TempData["Message"] = "Menu saved.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        Portfolio = await current.GetSelectedAsync();
        MenuUsage = await current.GetMenuUsageAsync();
        SavedCurrency = (await current.GetRegionAsync()).Currency;
        if (Portfolio is null || !Portfolio.CanEdit)
            return RedirectToPage();
        AccountingMode = await chart.GetAccountingModeAsync(Portfolio.PortfolioId);

        var errors = RegionSettings.Validate(CountryCode, YearStartMonth, YearStartDay, Currency, Format);
        foreach (var e in errors)
            ModelState.AddModelError(string.Empty, e);
        if (errors.Count > 0)
            return Page();

        var settings = new RegionSettings(CountryCode, new YearStart(YearStartMonth, YearStartDay), Currency, Format);
        var result = await portfolios.SaveRegionAsync(CurrentPortfolio.UserId(User), Portfolio.PortfolioId, settings);
        if (!result.Ok)
        {
            ModelState.AddModelError(string.Empty, result.Error!);
            return Page();
        }

        TempData["Message"] = $"Settings saved for \"{Portfolio.Name}\".";
        return RedirectToPage();
    }
}
