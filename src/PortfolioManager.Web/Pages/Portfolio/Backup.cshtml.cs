using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Backup;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Portfolio;

/// <summary>
/// Downloads the picked portfolio as a backup file (owners only, as it holds everything), and restores a backup
/// as a new portfolio of the signed-in login's, from this copy of the site or another.
/// </summary>
[RequestSizeLimit(MaxUploadBytes)]
[RequestFormLimits(MultipartBodyLengthLimit = MaxUploadBytes)]
public class BackupModel(CurrentPortfolio current, BackupService backups, ILogger<BackupModel> logger) : PageModel
{
    public const long MaxUploadBytes = 256L * 1024 * 1024;

    [BindProperty]
    public bool WithAttachments { get; set; } = true;

    [BindProperty]
    public IFormFile? Upload { get; set; }

    /// <summary>The new portfolio's name; blank means the backup's name with "(restored)".</summary>
    [BindProperty]
    public string? NewName { get; set; }

    public PortfolioSummary? Portfolio { get; private set; }
    public RestoreResult? Restored { get; private set; }
    public string? RestoredFrom { get; private set; }

    public async Task OnGetAsync() => Portfolio = await current.GetSelectedAsync();

    public async Task<IActionResult> OnPostDownloadAsync(CancellationToken cancel)
    {
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is not { CanShare: true })
            return RedirectToPage();

        var file = await backups.CreateAsync(Portfolio.PortfolioId, WithAttachments, $"{Request.Scheme}://{Request.Host}", cancel);
        Response.ContentType = "application/octet-stream";
        Response.Headers.ContentDisposition = new System.Net.Mime.ContentDisposition
        {
            FileName = BackupFile.FileName(Portfolio.Name, DateTime.Today),
        }.ToString();
        await file.WriteAsync(Response.Body, cancel);
        return new EmptyResult();
    }

    public async Task<IActionResult> OnPostRestoreAsync(CancellationToken cancel)
    {
        Portfolio = await current.GetSelectedAsync();
        if (Upload is not { Length: > 0 })
        {
            ModelState.AddModelError(nameof(Upload), "Choose a backup file (it ends in .pmbackup).");
            return Page();
        }

        BackupFile file;
        try
        {
            await using var stream = Upload.OpenReadStream();
            file = await BackupFile.ReadAsync(stream, cancel);
        }
        catch (BackupFormatException e)
        {
            ModelState.AddModelError(nameof(Upload), e.Message);
            return Page();
        }

        var name = string.IsNullOrWhiteSpace(NewName) ? RestoredName(file.PortfolioName) : NewName.Trim();
        if (name.Length > 100)
        {
            ModelState.AddModelError(nameof(NewName), "Keep the name to 100 characters or fewer.");
            return Page();
        }

        try
        {
            Restored = await backups.RestoreAsync(CurrentPortfolio.UserId(User), file, name, cancel);
        }
        catch (BackupFormatException e)
        {
            ModelState.AddModelError(nameof(Upload), e.Message + " Nothing was restored.");
            return Page();
        }
        catch (Exception e) when (e is Microsoft.Data.SqlClient.SqlException or InvalidOperationException)
        {
            logger.LogError(e, "Restoring a backup of {Name} failed", file.PortfolioName);
            ModelState.AddModelError(nameof(Upload),
                "The backup couldn't be restored, so nothing was added. If this copy of the site is behind the one the backup " +
                "came from, run its latest database migrations and try again. The error was logged.");
            return Page();
        }

        RestoredFrom = file.Source;
        current.Refresh();
        await current.TrySelectAsync(Restored.PortfolioId);
        Portfolio = await current.GetSelectedAsync();
        return Page();
    }

    public static string RestoredName(string? backupName)
    {
        var name = string.IsNullOrWhiteSpace(backupName) ? "Portfolio" : backupName.Trim();
        name = $"{name} (restored)";
        return name.Length > 100 ? name[..100] : name;
    }
}
