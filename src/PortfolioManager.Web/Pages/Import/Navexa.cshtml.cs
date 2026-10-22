using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Import;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Import;

// Two files of at most 1 MB each, or the same text sent back as base64 when confirming
[RequestSizeLimit(NavexaModel.MaxRequestBytes)]
[RequestFormLimits(MultipartBodyLengthLimit = NavexaModel.MaxRequestBytes)]
public class NavexaModel(CurrentPortfolio current, NavexaImportService importer, ILogger<NavexaModel> logger) : PageModel
{
    public const int MaxFileBytes = 1024 * 1024;
    public const int MaxRequestBytes = 4 * MaxFileBytes;

    [BindProperty]
    public IFormFile? TradesFile { get; set; }

    [BindProperty]
    public IFormFile? IncomeFile { get; set; }

    /// <summary>Tick when the file's exchange rates are foreign currency per 1 AUD rather than Navexa's AUD per 1 foreign unit.</summary>
    [BindProperty]
    public bool RatesForeignPerPortfolioUnit { get; set; }

    [BindProperty]
    public bool WithCash { get; set; }

    // The uploaded text, carried from the preview to the confirm step
    [BindProperty]
    public string? TradesData { get; set; }

    [BindProperty]
    public string? IncomeData { get; set; }

    public PortfolioSummary? Portfolio { get; private set; }
    public NavexaPreview? Preview { get; private set; }

    [TempData]
    public string? Done { get; set; }

    public async Task OnGetAsync() => Portfolio = await current.GetSelectedAsync();

    public async Task<IActionResult> OnPostPreviewAsync()
    {
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is null)
            return Page();

        var trades = await ReadUploadAsync(TradesFile, nameof(TradesFile));
        var income = await ReadUploadAsync(IncomeFile, nameof(IncomeFile));
        if (!ModelState.IsValid)
            return Page();
        if (trades is null && income is null)
        {
            ModelState.AddModelError("", "Choose a trades file, an income file, or both.");
            return Page();
        }

        Preview = await importer.PreviewAsync(Portfolio.PortfolioId, trades, income, RatesForeignPerPortfolioUnit);
        TradesData = Encode(trades);
        IncomeData = Encode(income);
        return Page();
    }

    public async Task<IActionResult> OnPostImportAsync()
    {
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is null)
            return Page();

        var trades = Decode(TradesData);
        var income = Decode(IncomeData);
        if (!ModelState.IsValid)
            return Page();

        // Parse and check for duplicates again, so anything added since the preview is not doubled up
        Preview = await importer.PreviewAsync(Portfolio.PortfolioId, trades, income, RatesForeignPerPortfolioUnit);
        if (Preview.TradesToImport + Preview.PayoutsToImport == 0)
        {
            ModelState.AddModelError("", "There is nothing left to import.");
            return Page();
        }

        try
        {
            var result = await importer.ImportAsync(Portfolio.PortfolioId, Preview, WithCash);
            logger.LogInformation("Navexa import into portfolio {PortfolioId}: {Trades} trades, {Payouts} payouts",
                Portfolio.PortfolioId, result.Trades, result.Payouts);
            Done = $"Imported {result.Trades} trade(s) and {result.Payouts} payout(s) into {Portfolio.Name}.";
            return RedirectToPage();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Navexa import into portfolio {PortfolioId} failed", Portfolio.PortfolioId);
            ModelState.AddModelError("", "The import failed and nothing was saved. " + ex.Message);
            return Page();
        }
    }

    private async Task<string?> ReadUploadAsync(IFormFile? file, string field)
    {
        if (file is null || file.Length == 0)
            return null;
        if (file.Length > MaxFileBytes)
        {
            ModelState.AddModelError(field, $"{file.FileName} is larger than {MaxFileBytes / 1024 / 1024} MB.");
            return null;
        }
        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (extension is not (".csv" or ".txt"))
        {
            ModelState.AddModelError(field, $"{file.FileName} isn't a CSV file. Export it from Navexa as CSV (not Excel).");
            return null;
        }

        using var reader = new StreamReader(file.OpenReadStream(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync();
    }

    private static string? Encode(string? text) => text is null ? null : Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    private string? Decode(string? data)
    {
        if (string.IsNullOrEmpty(data))
            return null;
        try
        {
            var bytes = Convert.FromBase64String(data);
            if (bytes.Length <= MaxFileBytes)
                return Encoding.UTF8.GetString(bytes);
        }
        catch (FormatException)
        {
        }
        ModelState.AddModelError("", "The preview data was damaged. Upload the files again.");
        return null;
    }

    public static string StatusBadge(ImportRowStatus status) => status switch
    {
        ImportRowStatus.Ready => "bg-success",
        ImportRowStatus.Duplicate => "bg-secondary",
        ImportRowStatus.Unsupported => "bg-warning text-dark",
        _ => "bg-danger",
    };

    public static string StatusText(ImportRowStatus status) => status switch
    {
        ImportRowStatus.Ready => "Import",
        ImportRowStatus.Duplicate => "Duplicate",
        ImportRowStatus.Unsupported => "Not supported",
        _ => "Error",
    };
}
