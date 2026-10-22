using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Imports;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Core.Prices;
using PortfolioManager.Core.Regions;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Import;

[RequestSizeLimit(CsvUpload.RequestLimit)]
[RequestFormLimits(MultipartBodyLengthLimit = CsvUpload.RequestLimit)]
public class TradesModel(CurrentPortfolio current, ImportService imports, FxRateService fx, ILogger<TradesModel> logger) : PageModel
{
    [BindProperty]
    public IFormFile? Upload { get; set; }

    [BindProperty]
    public bool WithCash { get; set; }

    /// <summary>A BrokerCsv format id: the standard file, a broker's export, or work it out.</summary>
    [BindProperty]
    public string Format { get; set; } = BrokerCsv.Auto;

    /// <summary>The exchange for broker rows that don't name one.</summary>
    [BindProperty]
    public string DefaultMarket { get; set; } = "ASX";

    /// <summary>The previewed file, posted back for the import.</summary>
    [BindProperty]
    public string? Data { get; set; }

    /// <summary>The portfolio the preview was for, so a switch in another tab can't redirect the import.</summary>
    [BindProperty]
    public int PreviewPortfolioId { get; set; }

    public PortfolioSummary? Portfolio { get; private set; }
    public ImportPreview<ShareTradeRow>? Preview { get; private set; }

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

        var trades = Preview.ValidRows.ToList();
        try
        {
            await imports.ImportTradesAsync(Portfolio.PortfolioId, trades, WithCash);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Share trade import failed for portfolio {PortfolioId}", Portfolio.PortfolioId);
            ModelState.AddModelError(string.Empty, LockedPeriodService.IsLockError(ex) ? LockedPeriodFilter.Message(ex) : "The import failed and nothing was saved. Check the file and try again.");
            return Page();
        }

        TempData["Message"] = $"Imported {trades.Count} trade{(trades.Count == 1 ? "" : "s")} into {Portfolio.Name}.";
        return RedirectToPage("/Holdings");
    }

    private async Task BuildPreviewAsync(string text)
    {
        var currency = RegionSettings.Current.Currency;
        var preview = BrokerCsv.Parse(Format, text, DateTime.Today, currency, DefaultMarket);
        if (preview.FileError is null)
        {
            preview = await FillExchangeRatesAsync(preview, currency);
            preview = ShareTradeCsv.FlagDuplicates(preview, await imports.GetExistingTradeKeysAsync(Portfolio!.PortfolioId));
        }
        Preview = preview;
    }

    /// <summary>Foreign trades with a rate of 1 take the FX close on file for their date, so the cost base is in the portfolio currency.</summary>
    private async Task<ImportPreview<ShareTradeRow>> FillExchangeRatesAsync(ImportPreview<ShareTradeRow> preview, string currency)
    {
        var lines = new List<ParsedLine<ShareTradeRow>>();
        foreach (var l in preview.Lines)
        {
            if (l.Value is { } row && row.ExchangeRate == 1m && !row.Currency.Equals(currency, StringComparison.OrdinalIgnoreCase)
                && await fx.GetAsync(row.Currency, currency, DateOnly.FromDateTime(row.Date)) is { } rate)
            {
                var warnings = l.Warnings.Where(w => !w.Contains("no exchange rate")).Append(
                    $"Exchange rate {rate.Rate:0.######} {row.Currency} per {currency} from the FX close on {rate.Date:d MMM yyyy}.").ToList();
                lines.Add(l with { Value = row with { ExchangeRate = rate.Rate }, Warnings = warnings });
            }
            else
                lines.Add(l);
        }
        return preview with { Lines = lines };
    }
}
