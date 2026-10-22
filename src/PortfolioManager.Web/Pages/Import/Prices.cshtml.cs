using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Imports;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Import;

/// <summary>End-of-day market prices, shared by every portfolio, so only the site admin may upload them.</summary>
[RequestSizeLimit(CsvUpload.RequestLimit)]
[RequestFormLimits(MultipartBodyLengthLimit = CsvUpload.RequestLimit)]
// Prices are shared by every portfolio and only site admins can upload them
[AllowViewOnly]
public class PricesModel(SiteAdmin admin, ImportService imports, ILogger<PricesModel> logger) : PageModel
{
    [BindProperty]
    public IFormFile? Upload { get; set; }

    [BindProperty]
    public string ExchangeCode { get; set; } = "ASX";

    [BindProperty]
    public string? Data { get; set; }

    public ImportPreview<SharePriceRow>? Preview { get; private set; }

    public IActionResult OnGet() => admin.IsAdmin(User) ? Page() : Forbid();

    public async Task<IActionResult> OnPostPreviewAsync()
    {
        if (!admin.IsAdmin(User))
            return Forbid();
        if (!CheckExchange())
            return Page();

        var (text, error) = await CsvUpload.ReadAsync(Upload);
        if (error is not null)
        {
            ModelState.AddModelError(string.Empty, error);
            return Page();
        }

        Preview = SharePriceCsv.Parse(text!);
        Data = CsvUpload.Encode(text!);
        return Page();
    }

    public async Task<IActionResult> OnPostImportAsync()
    {
        if (!admin.IsAdmin(User))
            return Forbid();
        if (!CheckExchange())
            return Page();
        if (CsvUpload.Decode(Data) is not { } text)
        {
            ModelState.AddModelError(string.Empty, "The preview couldn't be read back. Upload the file again.");
            return Page();
        }

        Preview = SharePriceCsv.Parse(text);
        if (Preview.FileError is not null || Preview.ValidCount == 0)
        {
            ModelState.AddModelError(string.Empty, "There are no valid prices to import.");
            return Page();
        }

        // Like the legacy upload, lines with errors are left out and the rest go in
        var prices = Preview.ValidRows.ToList();
        try
        {
            await imports.ImportSharePricesAsync(ExchangeCode, prices);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Share price import failed for {ExchangeCode}", ExchangeCode);
            ModelState.AddModelError(string.Empty, "The import failed and nothing was saved. Check the file and try again.");
            return Page();
        }

        TempData["Message"] = $"Imported {prices.Count:N0} {ExchangeCode.Trim().ToUpperInvariant()} prices"
            + (Preview.ErrorCount > 0 ? $" ({Preview.ErrorCount:N0} lines with errors were skipped)." : ".");
        return RedirectToPage();
    }

    private bool CheckExchange()
    {
        if (SharePriceCsv.CheckExchangeCode(ExchangeCode) is { } error)
        {
            ModelState.AddModelError(string.Empty, error);
            return false;
        }
        return true;
    }
}
