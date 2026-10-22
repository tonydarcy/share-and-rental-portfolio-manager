using Microsoft.AspNetCore.Mvc;
using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Data;
using PortfolioManager.Core.Tax;
using PortfolioManager.Core.TaxWork;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.TaxWork;

/// <summary>SMSF extras for the year: TBAR events, the actuary's ECPI details, the investment strategy and the pension minute.</summary>
public class SmsfExtrasModel(CurrentPortfolio current, TaxWorkpaperService workpapers, LockedPeriodService locks, SmsfExtrasService extras)
    : TaxYearPageModel(current, workpapers, locks)
{
    public sealed class EventInput
    {
        public int Id { get; set; }
        public int MemberId { get; set; }
        public DateOnly? Date { get; set; }
        public string Type { get; set; } = TbarEventTypes.Commenced;
        public decimal Amount { get; set; }
        public string? Note { get; set; }
        public DateOnly? Reported { get; set; }
    }

    public sealed class EcpiInput
    {
        public string? Method { get; set; }
        public decimal? ActuaryPct { get; set; }
        public string? Actuary { get; set; }
        public DateOnly? Certificate { get; set; }
    }

    [BindProperty] public EventInput Event { get; set; } = new();
    [BindProperty] public EcpiInput Ecpi { get; set; } = new();
    [BindProperty] public InvestmentStrategy Strategy { get; set; } = new();
    [BindProperty] public DateOnly? StrategyReviewed { get; set; }

    public IReadOnlyList<TbarEvent> Events { get; private set; } = [];
    public SmsfYearExtras Extras { get; private set; } = SmsfYearExtras.Empty;
    public (string? Method, bool NeedsCertificate, string Reason) Suggested { get; private set; }
    public IReadOnlyList<(string Name, decimal Balance)> Assets { get; private set; } = [];

    public bool IsSmsf => Workpaper?.EntityType == Core.Clients.ClientEntityTypes.Smsf;

    public async Task<IActionResult> OnGetAsync()
    {
        if (await LoadAsync() is { } refusal)
            return refusal;
        Events = await extras.GetEventsAsync(Portfolio!.PortfolioId);
        Extras = await extras.GetYearAsync(Portfolio.PortfolioId, Year);
        Strategy = Extras.Strategy;
        StrategyReviewed = Extras.StrategyReviewed;
        Ecpi = new EcpiInput { Method = Extras.EcpiMethod, ActuaryPct = Extras.ActuaryPct, Actuary = Extras.Actuary, Certificate = Extras.Certificate };
        Suggested = EcpiMethods.Suggest(Workpaper!.Smsf ?? SmsfYearData.Empty, Year);
        Assets = Workpaper.Ledger.Closing.Accounts.Where(a => a.Type == "Asset" && a.Balance != 0)
            .Select(a => (a.Name.Trim(), a.Balance)).OrderByDescending(a => a.Balance).ToList();
        return Page();
    }

    public async Task<IActionResult> OnPostEventAsync()
    {
        if (await LoadAsync() is { } refusal)
            return refusal;
        if (Event.Date is null)
            return Done(ProcResult.Failed("Enter the date of the event."), "");
        return Done(await extras.SaveEventAsync(Portfolio!.PortfolioId, Event.Id, Event.MemberId, Event.Date.Value, Event.Type, Event.Amount, Event.Note, Event.Reported),
            "TBAR event saved.");
    }

    public async Task<IActionResult> OnPostReportedAsync(int id, DateOnly? reported)
    {
        if (await LoadAsync() is { } refusal)
            return refusal;
        return Done(await extras.SetReportedAsync(Portfolio!.PortfolioId, id, reported ?? Today), "Marked as reported.");
    }

    public async Task<IActionResult> OnPostDeleteEventAsync(int id)
    {
        if (await LoadAsync() is { } refusal)
            return refusal;
        return Done(await extras.DeleteEventAsync(Portfolio!.PortfolioId, id), "TBAR event deleted.");
    }

    public async Task<IActionResult> OnPostEcpiAsync()
    {
        if (await LoadAsync() is { } refusal)
            return refusal;
        if (Locked)
            return Done(ProcResult.Failed($"{FinancialYear.Label(Year)} is locked."), "");
        return Done(await extras.SaveEcpiAsync(Portfolio!.PortfolioId, Year, Ecpi.Method is { Length: > 0 } m ? m : null, Ecpi.ActuaryPct, Ecpi.Actuary, Ecpi.Certificate),
            "ECPI details saved. Put the actuary's percentage in the tax settings too, so the return uses it.");
    }

    public async Task<IActionResult> OnPostStrategyAsync()
    {
        if (await LoadAsync() is { } refusal)
            return refusal;
        var problems = Strategy.Validate().ToList();
        if (problems.Count > 0)
            return Done(ProcResult.Failed(string.Join(" ", problems)), "");
        return Done(await extras.SaveStrategyAsync(Portfolio!.PortfolioId, Year, Strategy, StrategyReviewed), "Investment strategy saved.");
    }
}
