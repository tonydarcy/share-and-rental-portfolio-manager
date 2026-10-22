using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Core.Practice;
using PortfolioManager.Core.Tax;
using PortfolioManager.Core.Trades;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Attachments;

/// <summary>Files kept against a trade, a journal, the client or a year, in the picked portfolio.</summary>
public class IndexModel(CurrentPortfolio current, AttachmentService attachments, JournalService journals, TradesService trades) : PageModel
{
    [BindProperty(SupportsGet = true)] public string? Kind { get; set; }
    [BindProperty(SupportsGet = true)] public int? Target { get; set; }
    [BindProperty(SupportsGet = true)] public int? Fy { get; set; }
    [BindProperty] public IFormFile? Upload { get; set; }
    [BindProperty] public string? Note { get; set; }
    [TempData] public string? Message { get; set; }
    [TempData] public string? Error { get; set; }

    public PortfolioSummary? Portfolio { get; private set; }
    public IReadOnlyList<Attachment> Files { get; private set; } = [];
    public string? TargetText { get; private set; }
    public IReadOnlyList<int> Years { get; } = FinancialYear.Recent(DateOnly.FromDateTime(DateTime.Today), 8);

    public async Task<IActionResult> OnGetAsync()
    {
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is null)
            return Page();
        if (Kind is not null && !AttachmentKinds.All.Contains(Kind))
            Kind = null;
        if (Kind is AttachmentKinds.Trade or AttachmentKinds.Journal)
        {
            if (await DescribeAsync(Portfolio.PortfolioId) is not { } found)
                return NotFound();
            TargetText = found.Text;
        }
        else if (Kind is AttachmentKinds.Bill or AttachmentKinds.ExpenseClaim && Target is { } target)
            TargetText = $"{AttachmentKinds.Label(Kind)} {target}";
        Files = await attachments.ListAsync(Portfolio.PortfolioId, Kind, Target,
            Kind is AttachmentKinds.Trade or AttachmentKinds.Journal or AttachmentKinds.Bill or AttachmentKinds.ExpenseClaim ? null : Fy);
        return Page();
    }

    public async Task<IActionResult> OnGetDownloadAsync(int id)
    {
        if (await current.GetSelectedAsync() is not { } p || await attachments.GetFileAsync(p.PortfolioId, id) is not { } file)
            return NotFound();
        Response.Headers.XContentTypeOptions = "nosniff";
        return File(file.Data, file.ContentType, file.FileName);
    }

    public async Task<IActionResult> OnPostUploadAsync()
    {
        if (await current.GetSelectedAsync() is not { } p)
            return Forbid();
        var kind = Kind is not null && AttachmentKinds.All.Contains(Kind) ? Kind : AttachmentKinds.Year;
        if (Upload is null || AttachmentRules.Validate(Upload.FileName, Upload.Length) is { } invalid)
        {
            Error = Upload is null ? "Choose a file to upload." : AttachmentRules.Validate(Upload.FileName, Upload.Length);
            return Back();
        }
        int? year = Fy;
        if (kind is AttachmentKinds.Trade or AttachmentKinds.Journal)
        {
            if (await DescribeAsync(p.PortfolioId) is not { } found)
                return NotFound();
            year = FinancialYear.EndYearFor(found.Date);
        }
        else if (kind == AttachmentKinds.Client)
            year = null;
        else
            year ??= FinancialYear.EndYearFor(DateOnly.FromDateTime(DateTime.Today));

        using var ms = new MemoryStream();
        await Upload.CopyToAsync(ms);
        var target = kind is AttachmentKinds.Client or AttachmentKinds.Year ? null : Target;
        var r = await attachments.AddAsync(p.PortfolioId, CurrentPortfolio.UserId(User), kind, target, year, Upload.FileName, ms.ToArray(), Note);
        if (r.Ok) Message = $"{AttachmentRules.CleanName(Upload.FileName)} uploaded."; else Error = r.Error;
        return Back();
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        if (await current.GetSelectedAsync() is not { } p)
            return Forbid();
        var r = await attachments.DeleteAsync(p.PortfolioId, id);
        if (r.Ok) Message = "File deleted."; else Error = r.Error;
        return Back();
    }

    private IActionResult Back() => RedirectToPage(new { kind = Kind, target = Target, fy = Fy });

    /// <summary>The trade or journal, only when it is in the portfolio.</summary>
    private async Task<(string Text, DateOnly Date)?> DescribeAsync(int portfolioId)
    {
        if (Target is not { } id)
            return null;
        if (Kind == AttachmentKinds.Journal)
            return await journals.GetAsync(portfolioId, id) is { } j ? ($"Journal {j.JournalId}: {j.Description} ({j.Date:d})", j.Date) : null;
        return await trades.GetTradeAsync(portfolioId, id) is { } t
            ? ($"{t.Type} {t.Quantity:#,0.####} {t.StockCode} on {t.Date:d}", DateOnly.FromDateTime(t.Date))
            : null;
    }
}
