using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Crypto;
using PortfolioManager.Core.Imports;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Core.Prices;
using PortfolioManager.Core.Regions;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Import;

[RequestSizeLimit(CsvUpload.RequestLimit)]
[RequestFormLimits(MultipartBodyLengthLimit = CsvUpload.RequestLimit)]
public class CryptoModel(CurrentPortfolio current, CryptoService crypto, ImportService imports, FxRateService fx, ILogger<CryptoModel> logger) : PageModel
{
    [BindProperty]
    public IFormFile? Upload { get; set; }

    /// <summary>Buys and sells also settle in the bank account; swaps and rewards never move cash.</summary>
    [BindProperty]
    public bool WithCash { get; set; }

    [BindProperty]
    public string Format { get; set; } = CryptoExchangeCsv.Auto;

    /// <summary>The previewed file, posted back for the import.</summary>
    [BindProperty]
    public string? Data { get; set; }

    /// <summary>The portfolio the preview was for, so a switch in another tab can't redirect the import.</summary>
    [BindProperty]
    public int PreviewPortfolioId { get; set; }

    public PortfolioSummary? Portfolio { get; private set; }
    public CryptoPreview? Preview { get; private set; }

    public async Task OnGetAsync() => Portfolio = await current.GetSelectedAsync();

    public async Task<IActionResult> OnPostPreviewAsync()
    {
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is null)
            return Forbid();

        var (text, error) = await CsvUpload.ReadAsync(Upload);
        if (error is not null)
        {
            ModelState.AddModelError(string.Empty, error);
            return Page();
        }

        await BuildPreviewAsync(text!);
        Data = CsvUpload.Encode(text!);
        PreviewPortfolioId = Portfolio.PortfolioId;
        return Page();
    }

    public async Task<IActionResult> OnPostImportAsync()
    {
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is null)
            return Forbid();

        if (PreviewPortfolioId != Portfolio.PortfolioId)
        {
            ModelState.AddModelError(string.Empty, "The portfolio changed after the preview. Upload the file again to import it into this portfolio.");
            return Page();
        }
        if (CsvUpload.Decode(Data) is not { } text)
        {
            ModelState.AddModelError(string.Empty, "The preview couldn't be read back. Upload the file again.");
            return Page();
        }

        await BuildPreviewAsync(text);
        if (Preview!.FileError is not null || Preview.ErrorCount > 0)
        {
            ModelState.AddModelError(string.Empty, "Fix the lines with errors and upload the file again. Nothing was imported.");
            return Page();
        }

        var rows = Preview.ValidRows.ToList();
        try
        {
            var result = await crypto.ImportAsync(Portfolio.PortfolioId, rows, WithCash);
            if (!result.Ok)
            {
                ModelState.AddModelError(string.Empty, $"Nothing was imported. {result.Error}");
                return Page();
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Crypto import failed for portfolio {PortfolioId}", Portfolio.PortfolioId);
            ModelState.AddModelError(string.Empty, LockedPeriodService.IsLockError(ex) ? LockedPeriodFilter.Message(ex) : "The import failed and nothing was saved. Check the file and try again.");
            return Page();
        }

        TempData["Message"] = $"Imported {rows.Count} line{(rows.Count == 1 ? "" : "s")} from {Preview.Source} into {Portfolio.Name}.";
        return RedirectToPage("/Holdings");
    }

    private async Task BuildPreviewAsync(string text)
    {
        var currency = RegionSettings.Current.Currency;
        var preview = CryptoExchangeCsv.Parse(Format, text, DateTime.Today, currency);
        if (preview.FileError is null)
        {
            preview = await FillExchangeRatesAsync(preview, currency);
            preview = CryptoExchangeCsv.FlagDuplicates(preview, await imports.GetExistingTradeKeysAsync(Portfolio!.PortfolioId));
        }
        Preview = preview;
    }

    /// <summary>Trades priced in another currency (BTC bought for USD) take the FX close on file for their date.</summary>
    private async Task<CryptoPreview> FillExchangeRatesAsync(CryptoPreview preview, string currency)
    {
        var lines = new List<CryptoLine>();
        foreach (var l in preview.Lines)
        {
            if (l.Row?.Trade is { } t && !t.Currency.Equals(currency, StringComparison.OrdinalIgnoreCase)
                && await fx.GetAsync(t.Currency, currency, t.Date) is { } rate)
            {
                var warnings = l.Warnings.Where(w => !w.Contains("exchange rate")).Append(
                    $"Exchange rate {rate.Rate:0.######} {t.Currency} per {currency} from the FX close on {rate.Date:d MMM yyyy}.").ToList();
                lines.Add(l with { Row = l.Row with { Trade = t with { ExchangeRate = rate.Rate } }, Warnings = warnings });
            }
            else
                lines.Add(l);
        }
        return preview with { Lines = lines };
    }
}
