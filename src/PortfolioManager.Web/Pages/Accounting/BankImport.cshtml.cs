using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Accounting;

[RequestSizeLimit(CsvUpload.RequestLimit)]
[RequestFormLimits(MultipartBodyLengthLimit = CsvUpload.RequestLimit)]
public class BankImportModel(CurrentPortfolio current, BankImportService imports, ChartOfAccountsService chart, AllocationRuleService rules, ILogger<BankImportModel> logger) : PageModel
{
    [BindProperty]
    public IFormFile? Upload { get; set; }

    [BindProperty]
    public int BankAccountId { get; set; }

    /// <summary>Also import lines that look like ones already imported into this account.</summary>
    [BindProperty]
    public bool IncludeDuplicates { get; set; }

    /// <summary>Allocate the imported lines that match an allocation rule straight away.</summary>
    [BindProperty]
    public bool ApplyRules { get; set; } = true;

    /// <summary>The previewed file, posted back for the import.</summary>
    [BindProperty]
    public string? Data { get; set; }

    [BindProperty]
    public string? FileName { get; set; }

    /// <summary>The portfolio the preview was for, so a switch in another tab can't redirect the import.</summary>
    [BindProperty]
    public int PreviewPortfolioId { get; set; }

    public PortfolioSummary? Portfolio { get; private set; }
    public string Mode { get; private set; } = "";
    public IReadOnlyList<AccountChoice> BankAccounts { get; private set; } = [];
    public BankStatement? Preview { get; private set; }

    public string BankAccountName => BankAccounts.FirstOrDefault(a => a.AccountId == BankAccountId)?.DisplayName ?? "";

    public async Task OnGetAsync()
    {
        if (await LoadAsync() && BankAccounts.Count > 0)
            BankAccountId = BankAccounts[0].AccountId;
    }

    public async Task<IActionResult> OnPostPreviewAsync()
    {
        if (!await LoadAsync())
            return Forbid();

        if (BankAccounts.All(a => a.AccountId != BankAccountId))
        {
            ModelState.AddModelError(string.Empty, "Choose the account the statement belongs to.");
            return Page();
        }
        if (BankStatementParser.CheckFile(Upload?.Length ?? 0, Upload?.FileName) is { } fileError)
        {
            ModelState.AddModelError(string.Empty, fileError);
            return Page();
        }

        using var stream = new MemoryStream();
        await Upload!.CopyToAsync(stream);
        var text = BankStatementParser.Decode(stream.ToArray());
        FileName = Path.GetFileName(Upload.FileName);
        await BuildPreviewAsync(text);
        Data = CsvUpload.Encode(text);
        PreviewPortfolioId = Portfolio!.PortfolioId;
        return Page();
    }

    public async Task<IActionResult> OnPostImportAsync()
    {
        if (!await LoadAsync())
            return Forbid();

        if (PreviewPortfolioId != Portfolio!.PortfolioId)
        {
            ModelState.AddModelError(string.Empty, "The portfolio changed after the preview. Upload the statement again to import it into this portfolio.");
            return Page();
        }
        if (BankAccounts.All(a => a.AccountId != BankAccountId))
        {
            ModelState.AddModelError(string.Empty, "Choose the account the statement belongs to.");
            return Page();
        }
        if (CsvUpload.Decode(Data) is not { } text)
        {
            ModelState.AddModelError(string.Empty, "The preview couldn't be read back. Upload the statement again.");
            return Page();
        }

        await BuildPreviewAsync(text);
        if (Preview!.FileError is not null || Preview.ErrorCount > 0)
        {
            ModelState.AddModelError(string.Empty, "Some lines couldn't be read, so nothing was imported. Check the file and upload it again.");
            return Page();
        }

        var transactions = Preview.Lines
            .Where(l => l.IsValid && (IncludeDuplicates || !l.Duplicate))
            .Select(l => l.Transaction!)
            .ToList();
        if (transactions.Count == 0)
        {
            TempData["Message"] = "Every line in the statement has already been imported, so nothing new was added.";
            return RedirectToPage("/Accounting/ImportHistory");
        }

        try
        {
            await imports.ImportAsync(Portfolio.PortfolioId, BankAccountId, transactions);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Bank statement import failed for portfolio {PortfolioId}", Portfolio.PortfolioId);
            ModelState.AddModelError(string.Empty, LockedPeriodService.IsLockError(ex) ? LockedPeriodFilter.Message(ex) : "The import failed and nothing was saved. Check the file and try again.");
            return Page();
        }

        var skipped = Preview.ValidCount - transactions.Count;
        RulesApplied? applied = null;
        try
        {
            if (ApplyRules)
                applied = await rules.ApplyAsync(Portfolio.PortfolioId);
        }
        catch (Exception ex)
        {
            // The statement is already imported; the lines just stay unallocated
            logger.LogError(ex, "Applying allocation rules after an import failed for portfolio {PortfolioId}", Portfolio.PortfolioId);
        }
        TempData["Message"] = $"Imported {transactions.Count} transaction{(transactions.Count == 1 ? "" : "s")} into {BankAccountName}" +
                              (skipped > 0 ? $", skipping {skipped} already imported." : ".") +
                              (applied is { Allocated: > 0 } ? $" Allocation rules allocated {applied.Allocated}." : "") +
                              " Allocate the rest on the Unallocated page.";
        return RedirectToPage("/Accounting/Unallocated");
    }

    private async Task<bool> LoadAsync()
    {
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is null)
            return false;
        BankAccounts = await chart.GetImportAccountsAsync(Portfolio.PortfolioId);
        Mode = await chart.GetAccountingModeAsync(Portfolio.PortfolioId);
        return true;
    }

    private async Task BuildPreviewAsync(string text)
    {
        var statement = BankStatementParser.Parse(text, FileName);
        var dates = statement.Transactions.Select(t => t.Date).ToList();
        if (statement.FileError is null && dates.Count > 0)
        {
            var existing = await imports.GetImportedAsync(Portfolio!.PortfolioId, BankAccountId, dates.Min(), dates.Max());
            statement = BankStatementParser.FlagDuplicates(statement, existing);
        }
        Preview = statement;
    }
}
