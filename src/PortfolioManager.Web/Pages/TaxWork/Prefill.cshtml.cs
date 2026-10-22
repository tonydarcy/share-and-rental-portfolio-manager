using Microsoft.AspNetCore.Mvc;
using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Data;
using PortfolioManager.Core.Imports;
using PortfolioManager.Core.TaxWork;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.TaxWork;

/// <summary>Import the ATO prefill report for the year and compare it with the payouts and statements.</summary>
public class PrefillModel(CurrentPortfolio current, TaxWorkpaperService workpapers, LockedPeriodService locks, PrefillService prefill)
    : TaxYearPageModel(current, workpapers, locks)
{
    [BindProperty] public string? Pasted { get; set; }
    [BindProperty] public IFormFile? Upload { get; set; }

    public IReadOnlyList<PrefillItem> Items { get; private set; } = [];
    public DateTime? Imported { get; private set; }
    public IReadOnlyList<PrefillComparison> Comparison { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync()
    {
        if (await LoadAsync() is { } refusal)
            return refusal;
        (Items, Imported) = await prefill.GetAsync(Portfolio!.PortfolioId, Year);
        Comparison = PrefillParser.Compare(Items, Workpaper!);
        return Page();
    }

    public async Task<IActionResult> OnPostImportAsync()
    {
        if (await LoadAsync() is { } refusal)
            return refusal;
        var text = Pasted;
        if (Upload is { Length: > 0 })
        {
            if (Upload.Length > CsvImport.MaxFileBytes)
                return Done(ProcResult.Failed("That file is too big."), "");
            using var reader = new StreamReader(Upload.OpenReadStream());
            text = await reader.ReadToEndAsync();
        }
        if (string.IsNullOrWhiteSpace(text))
            return Done(ProcResult.Failed("Choose a CSV file or paste the figures."), "");
        var (items, problems) = PrefillParser.Parse(text);
        if (items.Count == 0)
            return Done(ProcResult.Failed(string.Join(" ", problems)), "");
        var r = await prefill.ReplaceAsync(Portfolio!.PortfolioId, Year, items);
        if (r.Ok && problems.Count > 0)
            TempData["Error"] = string.Join(" ", problems.Take(10));
        return Done(r, $"Imported {items.Count} prefill figure{(items.Count == 1 ? "" : "s")}, replacing any from before.");
    }

    public async Task<IActionResult> OnPostClearAsync()
    {
        if (await LoadAsync() is { } refusal)
            return refusal;
        return Done(await prefill.ReplaceAsync(Portfolio!.PortfolioId, Year, []), "Prefill figures cleared.");
    }
}
