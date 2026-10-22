using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Core.Regions;
using PortfolioManager.Core.SalesTax;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.SalesTax;

/// <summary>
/// The GST / VAT / sales tax report for a period: the boxes of the return, the totals per tax
/// code and every transaction, on screen, printed or as CSV.
/// </summary>
public class IndexModel(CurrentPortfolio current, SalesTaxService salesTax) : PageModel
{
    public static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);

    /// <summary>A period from the list, by its start date (yyyy-MM-dd).</summary>
    [BindProperty(SupportsGet = true)]
    public string? Period { get; set; }

    [BindProperty(SupportsGet = true)]
    public DateOnly? From { get; set; }

    [BindProperty(SupportsGet = true)]
    public DateOnly? To { get; set; }

    public PortfolioSummary? Portfolio { get; private set; }
    public SalesTaxSetup? Setup { get; private set; }
    public SalesTaxPreset Preset { get; private set; } = SalesTaxPresets.For(RegionSettings.AustraliaCode);
    public IReadOnlyList<TaxPeriod> Periods { get; private set; } = [];
    public TaxPeriod? Selected { get; private set; }
    public SalesTaxReport? Report { get; private set; }
    public string? Warning { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnGetCsvAsync()
    {
        await LoadAsync();
        if (Report is null)
            return RedirectToPage();
        var name = new string(Report.TaxName.Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray()).Trim('-').ToLowerInvariant();
        return File(Report.ToCsv(), "text/csv", $"{(name.Length > 0 ? name : "sales-tax")}-report-{Report.From:yyyy-MM-dd}-to-{Report.To:yyyy-MM-dd}.csv");
    }

    private async Task LoadAsync()
    {
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is null)
            return;
        var region = await current.GetRegionAsync();
        Preset = SalesTaxPresets.For(region.CountryCode);
        Setup = await salesTax.GetSetupAsync(Portfolio.PortfolioId);
        if (Setup is null)
            return;

        Periods = TaxPeriods.Recent(Today, Setup.ReportingMonths, region.YearStart);
        var (from, to, warning) = TaxPeriods.Resolve(Period, From, To, Periods, TaxPeriods.LastCompleted(Today, Setup.ReportingMonths, region.YearStart));
        Warning = warning;
        Selected = Periods.FirstOrDefault(p => p.From == from && p.To == to);
        From = from;
        To = to;
        Report = await salesTax.GetReportAsync(Portfolio.PortfolioId, Setup.TaxName, from, to);
    }
}
