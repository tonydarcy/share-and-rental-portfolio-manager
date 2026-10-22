using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Core.Regions;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Portfolio;

/// <summary>
/// The picked portfolio's country, financial year start, currency and date/number format.
/// Australia is the default; only Australia gets the franking and CGT discount wording.
/// </summary>
public class SettingsModel(CurrentPortfolio current, PortfolioService portfolios) : PageModel
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

    public async Task OnGetAsync()
    {
        Portfolio = await current.GetSelectedAsync();
        var region = await current.GetRegionAsync();
        CountryCode = region.CountryCode;
        YearStartMonth = region.YearStart.Month;
        YearStartDay = region.YearStart.Day;
        Currency = region.Currency;
        Format = region.Format;
        SavedCurrency = region.Currency;
    }

    public async Task<IActionResult> OnPostAsync()
    {
        Portfolio = await current.GetSelectedAsync();
        SavedCurrency = (await current.GetRegionAsync()).Currency;
        if (Portfolio is null || !Portfolio.CanEdit)
            return RedirectToPage();

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
