using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.CorporateActions;
using PortfolioManager.Core.Imports;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Import;

/// <summary>
/// Franking and DRP prices for announced dividends, which the daily feed doesn't have. Announced dividends are shared by
/// every portfolio, so only the site admin may upload them.
/// </summary>
[RequestSizeLimit(CsvUpload.RequestLimit)]
[RequestFormLimits(MultipartBodyLengthLimit = CsvUpload.RequestLimit)]
[AllowViewOnly]
public class DividendDetailsModel(SiteAdmin admin, AnnouncedActionsService announced, ILogger<DividendDetailsModel> logger) : PageModel
{
    [BindProperty]
    public IFormFile? Upload { get; set; }

    /// <summary>Where the figures came from, shown next to the franking (e.g. "ASX announcement").</summary>
    [BindProperty]
    public string Source { get; set; } = "Dividend details file";

    [BindProperty]
    public string? Data { get; set; }

    public ImportPreview<DividendDetailsRow>? Preview { get; private set; }

    public IActionResult OnGet() => admin.IsAdmin(User) ? Page() : Forbid();

    public async Task<IActionResult> OnPostPreviewAsync()
    {
        if (!admin.IsAdmin(User))
            return Forbid();
        if (!CheckSource())
            return Page();
        var (text, error) = await CsvUpload.ReadAsync(Upload);
        if (error is not null)
        {
            ModelState.AddModelError(string.Empty, error);
            return Page();
        }
        Preview = DividendDetailsCsv.Parse(text!);
        Data = CsvUpload.Encode(text!);
        return Page();
    }

    public async Task<IActionResult> OnPostImportAsync()
    {
        if (!admin.IsAdmin(User))
            return Forbid();
        if (!CheckSource())
            return Page();
        if (CsvUpload.Decode(Data) is not { } text)
        {
            ModelState.AddModelError(string.Empty, "The preview couldn't be read back. Upload the file again.");
            return Page();
        }
        Preview = DividendDetailsCsv.Parse(text);
        if (Preview.FileError is not null || Preview.ValidCount == 0)
        {
            ModelState.AddModelError(string.Empty, "There are no valid lines to import.");
            return Page();
        }
        try
        {
            var (updated, added, errors) = await announced.SaveDetailsAsync(Preview.ValidRows, Source.Trim());
            TempData["Message"] = $"Updated {updated:N0} announced dividends and added {added:N0}."
                + (errors.Count > 0 ? $" {errors.Count:N0} couldn't be saved: {string.Join(" ", errors.Take(5))}" : "")
                + (Preview.ErrorCount > 0 ? $" {Preview.ErrorCount:N0} lines with errors were skipped." : "");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Dividend details import failed");
            ModelState.AddModelError(string.Empty, "The import failed. Check the file and try again.");
            return Page();
        }
        return RedirectToPage();
    }

    private bool CheckSource()
    {
        if (string.IsNullOrWhiteSpace(Source) || Source.Trim().Length > 100)
        {
            ModelState.AddModelError(nameof(Source), "Say where the figures came from, in up to 100 characters.");
            return false;
        }
        return true;
    }
}
