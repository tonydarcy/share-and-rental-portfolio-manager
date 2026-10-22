using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Core.Regions;
using PortfolioManager.Core.SalesTax;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.SalesTax;

/// <summary>Sales tax setup (name, period, control account) and the portfolio's tax codes (?id= edits one).</summary>
public class CodesModel(CurrentPortfolio current, SalesTaxService salesTax, ChartOfAccountsService chart) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public int? Id { get; set; }

    [BindProperty]
    public SalesTaxSetupInput Setup { get; set; } = new();

    [BindProperty]
    public TaxCodeInput Input { get; set; } = new();

    public PortfolioSummary? Portfolio { get; private set; }
    public SalesTaxSetup? Current { get; private set; }
    public SalesTaxPreset Preset { get; private set; } = SalesTaxPresets.For(RegionSettings.AustraliaCode);
    public RegionSettings Region { get; private set; } = RegionSettings.Australia;
    public IReadOnlyList<TaxCode> Codes { get; private set; } = [];
    public IReadOnlyList<LedgerAccount> ControlAccounts { get; private set; } = [];

    public string TaxName => Current?.TaxName ?? Preset.TaxName;

    public async Task<IActionResult> OnGetAsync()
    {
        if (await LoadAsync() is { } refusal)
            return refusal;
        Setup = Current is { } s
            ? new SalesTaxSetupInput { TaxName = s.TaxName, ReportingMonths = s.ReportingMonths, ControlAccountId = s.ControlAccountId }
            : new SalesTaxSetupInput { TaxName = Preset.TaxName, ReportingMonths = Preset.ReportingMonths };
        if (Id is { } id && Codes.FirstOrDefault(c => c.TaxCodeId == id) is { } code)
            Input = TaxCodeInput.From(code);
        else
            Input = new TaxCodeInput { AmountIncludesTax = Preset.AmountsIncludeTax };
        return Page();
    }

    public async Task<IActionResult> OnPostSetupAsync()
    {
        if (await LoadAsync() is { } refusal)
            return refusal;
        var errors = Setup.Validate();
        var result = errors.Count == 0 ? await salesTax.SaveSetupAsync(Portfolio!.PortfolioId, Setup) : null;
        if (result is not { Ok: true })
        {
            foreach (var error in errors.Count > 0 ? errors : [result!.Error!])
                ModelState.AddModelError(string.Empty, error);
            return Page();
        }
        var first = Current is null;
        TempData["Message"] = first
            ? $"{Setup.TaxName.Trim()} is set up. Now add your tax codes: the usual ones for {Region.Country.Name} are one click away."
            : "Settings saved.";
        return RedirectToPage(new { id = (int?)null });
    }

    public async Task<IActionResult> OnPostPresetAsync()
    {
        if (await LoadAsync() is { } refusal)
            return refusal;
        if (Current is null)
        {
            TempData["Error"] = "Save the settings above first.";
            return RedirectToPage(new { id = (int?)null });
        }
        var added = await salesTax.AddPresetCodesAsync(Portfolio!.PortfolioId, Preset);
        TempData["Message"] = added == 0 ? "You already have all the usual codes." : $"Added {added} tax code{(added == 1 ? "" : "s")}. Check the rates and boxes suit you.";
        return RedirectToPage(new { id = (int?)null });
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        if (await LoadAsync() is { } refusal)
            return refusal;
        if (Current is null)
        {
            ModelState.AddModelError(string.Empty, "Save the settings above first.");
            return Page();
        }
        var errors = Input.Validate(Codes);
        var result = errors.Count == 0 ? await salesTax.SaveCodeAsync(Portfolio!.PortfolioId, Input) : null;
        if (result is not { Ok: true })
        {
            foreach (var error in errors.Count > 0 ? errors : [result!.Error!])
                ModelState.AddModelError(string.Empty, error);
            return Page();
        }
        TempData["Message"] = Input.TaxCodeId is null ? "Tax code added." : "Tax code saved.";
        return RedirectToPage(new { id = (int?)null });
    }

    public async Task<IActionResult> OnPostDeleteAsync(int taxCodeId)
    {
        if (await LoadAsync() is { } refusal)
            return refusal;
        var result = await salesTax.DeleteCodeAsync(Portfolio!.PortfolioId, taxCodeId);
        if (result.Ok)
            TempData["Message"] = "Tax code deleted.";
        else
            TempData["Error"] = result.Error;
        return RedirectToPage(new { id = (int?)null });
    }

    private async Task<IActionResult?> LoadAsync()
    {
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is null)
            return HttpMethods.IsGet(Request.Method) ? Page() : Forbid();
        Region = await current.GetRegionAsync();
        Preset = SalesTaxPresets.For(Region.CountryCode);
        Current = await salesTax.GetSetupAsync(Portfolio.PortfolioId);
        Codes = await salesTax.GetCodesAsync(Portfolio.PortfolioId);
        ControlAccounts = (await chart.GetAccountsAsync(Portfolio.PortfolioId))
            .Where(a => a.Type is "Asset" or "Liability" && !a.IsInactive
                        && a.Name != BankImportService.SuspenseAccountName && a.Name != "Trade Clearing / Suspense")
            .OrderBy(a => a.Number)
            .ToList();
        return null;
    }
}
