using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Clients;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Core.Practice;
using PortfolioManager.Core.Statements;
using PortfolioManager.Core.Tax;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.TaxWork;

/// <summary>The year-end file for the picked client: its status, the checklist for its entity type and a workpaper per balance.</summary>
public class WorkpapersModel(CurrentPortfolio current, YearEndService yearEnd, ClientService clients, LedgerReportService reports,
    ChartOfAccountsService chart, LockedPeriodService locks, AttachmentService attachments) : PageModel
{
    public sealed class TickInput
    {
        public string Key { get; set; } = "";
        public bool Done { get; set; }
        public string? Note { get; set; }
    }

    [BindProperty(SupportsGet = true)] public int? Fy { get; set; }
    [BindProperty] public string? Status { get; set; }
    [BindProperty] public string? StatusNote { get; set; }
    [BindProperty] public List<TickInput> Ticks { get; set; } = [];
    [TempData] public string? Message { get; set; }
    [TempData] public string? Error { get; set; }

    public PortfolioSummary? Portfolio { get; private set; }
    public string EntityType { get; private set; } = ClientEntityTypes.Individual;
    public int Year { get; private set; }
    public IReadOnlyList<int> Years { get; private set; } = [];
    public bool Locked { get; private set; }
    public string EffectiveStatus { get; private set; } = YearStatuses.NotStarted;
    public IReadOnlyList<ChecklistItem> Items { get; private set; } = [];
    public IReadOnlyDictionary<string, ChecklistTick> Done { get; private set; } = new Dictionary<string, ChecklistTick>();
    public IReadOnlyList<WorkpaperRow> Rows { get; private set; } = [];
    public IReadOnlyDictionary<int, int> Files { get; private set; } = new Dictionary<int, int>();

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostStatusAsync()
    {
        if (!await LoadAsync())
            return Forbid();
        var r = await yearEnd.SaveStatusAsync(Portfolio!.PortfolioId, Year, Status ?? "", StatusNote, CurrentPortfolio.UserId(User));
        if (r.Ok) Message = $"Status set to {YearStatuses.Label(Status!).ToLowerInvariant()}."; else Error = r.Error;
        return RedirectToPage(new { fy = Year });
    }

    public async Task<IActionResult> OnPostChecklistAsync()
    {
        if (!await LoadAsync())
            return Forbid();
        var keys = Items.Select(i => i.Key).ToHashSet();
        var saved = 0;
        foreach (var t in Ticks.Where(t => keys.Contains(t.Key)))
        {
            var before = Done.GetValueOrDefault(t.Key);
            if ((before?.Done ?? false) == t.Done && (before?.Note ?? "") == (t.Note?.Trim() ?? ""))
                continue;
            var r = await yearEnd.SaveChecklistItemAsync(Portfolio!.PortfolioId, Year, t.Key, t.Done, t.Note, CurrentPortfolio.UserId(User));
            if (!r.Ok) { Error = r.Error; break; }
            saved++;
        }
        Message ??= saved == 0 ? "Nothing changed." : "Checklist saved.";
        return RedirectToPage(new { fy = Year });
    }

    public async Task<IActionResult> OnPostPrepareAsync(int accountId, string? note, decimal? supported)
    {
        if (!await LoadAsync())
            return Forbid();
        if (Rows.All(r => r.AccountId != accountId))
            return NotFound();
        var r = await yearEnd.SaveWorkpaperAsync(Portfolio!.PortfolioId, Year, accountId, note, supported, CurrentPortfolio.UserId(User));
        if (r.Ok) Message = "Workpaper saved. It needs a reviewer's sign-off."; else Error = r.Error;
        return RedirectToPage(null, null, new { fy = Year }, $"wp{accountId}");
    }

    public async Task<IActionResult> OnPostReviewAsync(int id, bool signOff, string? reviewNote)
    {
        if (!await LoadAsync())
            return Forbid();
        var r = await yearEnd.ReviewWorkpaperAsync(Portfolio!.PortfolioId, id, signOff, reviewNote, CurrentPortfolio.UserId(User));
        if (r.Ok) Message = signOff ? "Signed off." : "Sign-off removed."; else Error = r.Error;
        return RedirectToPage(new { fy = Year });
    }

    private async Task<bool> LoadAsync()
    {
        Portfolio = await current.GetSelectedAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);
        Years = FinancialYear.Recent(today, 6);
        Year = Fy is { } y && Years.Contains(y) ? y : FinancialYear.LastCompleted(today);
        if (Portfolio is null)
            return false;
        var id = Portfolio.PortfolioId;
        EntityType = (await clients.GetAsync(id))?.EntityType ?? ClientEntityTypes.Individual;
        Locked = (await locks.GetAsync(id)).IsYearLocked(Year);
        var (status, note) = await yearEnd.GetStatusAsync(id, Year);
        EffectiveStatus = YearStatuses.Effective(status, Locked);
        Status = status ?? YearStatuses.NotStarted;
        StatusNote = note;
        Items = YearEndChecklist.For(EntityType);
        Done = (await yearEnd.GetChecklistAsync(id, Year)).ToDictionary(t => t.Key);
        var end = FinancialYear.EndDate(Year);
        var snapshot = LedgerSnapshot.FromTrialBalance(end, await reports.TrialBalanceAsync(id, end));
        var papers = await yearEnd.GetWorkpapersAsync(id, Year);
        Rows = Workpapers.Rows(snapshot, await chart.GetAccountsAsync(id), papers);
        Files = await attachments.CountsAsync(id, AttachmentKinds.Workpaper);
        return true;
    }
}
