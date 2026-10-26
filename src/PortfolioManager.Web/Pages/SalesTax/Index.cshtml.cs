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
public class IndexModel(CurrentPortfolio current, SalesTaxService salesTax, ActivityStatementService bas) : PageModel
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
    public BasSettings Bas { get; private set; } = BasSettings.Default;
    public ActivityStatement? Statement { get; private set; }
    public IReadOnlyDictionary<string, decimal> Entered { get; private set; } = new Dictionary<string, decimal>();
    public decimal SuggestedT1 { get; private set; }

    /// <summary>The full activity statement (W, T, F labels and the summary) is Australian.</summary>
    public bool IsBas { get; private set; }

    [BindProperty] public bool CashBasis { get; set; }
    [BindProperty] public string PaygMethod { get; set; } = PaygMethods.None;
    [BindProperty] public decimal? PaygAmount { get; set; }
    [BindProperty] public decimal? PaygRate { get; set; }
    [BindProperty] public Dictionary<string, string?> Labels { get; set; } = [];

    public async Task<IActionResult> OnPostSettingsAsync()
    {
        if (await current.GetSelectedAsync() is not { } p)
            return Forbid();
        var r = await bas.SaveSettingsAsync(p.PortfolioId, new BasSettings(CashBasis, PaygMethods.All.Contains(PaygMethod) ? PaygMethod : PaygMethods.None, PaygAmount, PaygRate));
        TempData[r.Ok ? "Message" : "Error"] = r.Ok ? "Activity statement settings saved." : r.Error;
        return RedirectToPage(new { Period, From = From?.ToString("yyyy-MM-dd"), To = To?.ToString("yyyy-MM-dd") });
    }

    /// <summary>Saves the typed labels for the period shown; a blank box clears that label.</summary>
    public async Task<IActionResult> OnPostLabelsAsync()
    {
        if (await current.GetSelectedAsync() is not { } p || From is not { } from || To is not { } to)
            return Forbid();
        var old = await bas.GetLabelsAsync(p.PortfolioId, from);
        var errors = new List<string>();
        foreach (var (label, text) in Labels)
        {
            if (!ActivityStatementBuilder.IsEntered(label))
                continue;
            decimal? amount = null;
            if (!string.IsNullOrWhiteSpace(text))
            {
                if (!decimal.TryParse(text, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var v))
                {
                    errors.Add($"{label} isn't a number.");
                    continue;
                }
                amount = v;
            }
            if (old.TryGetValue(label, out var was) ? was == amount : amount is null)
                continue;
            var r = await bas.SaveLabelAsync(p.PortfolioId, from, to, label, amount);
            if (!r.Ok)
                errors.Add(r.Error!);
        }
        TempData[errors.Count == 0 ? "Message" : "Error"] = errors.Count == 0 ? "Activity statement amounts saved." : string.Join(" ", errors.Distinct());
        return RedirectToPage(new { Period, From = from.ToString("yyyy-MM-dd"), To = to.ToString("yyyy-MM-dd") });
    }

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
        Bas = await bas.GetSettingsAsync(Portfolio.PortfolioId);
        Report = await bas.GetGstAsync(Portfolio.PortfolioId, Setup.TaxName, from, to, Bas.CashBasis);
        IsBas = region.IsAustralia;
        if (IsBas)
        {
            Entered = await bas.GetLabelsAsync(Portfolio.PortfolioId, from);
            SuggestedT1 = Bas.PaygMethod == PaygMethods.Rate ? await bas.SuggestInstalmentIncomeAsync(Portfolio.PortfolioId, from, to) : 0;
            Statement = ActivityStatementBuilder.Build(Report, Bas, Entered, SuggestedT1);
        }
    }
}
