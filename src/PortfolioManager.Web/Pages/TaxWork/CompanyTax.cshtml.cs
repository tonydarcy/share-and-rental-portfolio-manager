using Microsoft.AspNetCore.Mvc;
using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.TaxWork;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.TaxWork;

/// <summary>Company tax payable, deferred tax and the franking account for a financial year.</summary>
public class CompanyTaxModel(CurrentPortfolio current, TaxWorkpaperService workpapers, LockedPeriodService locks, FrankingAccountService franking)
    : TaxYearPageModel(current, workpapers, locks)
{
    [BindProperty]
    public FrankingEntryInput Entry { get; set; } = new();

    public FrankingLedger? Franking { get; private set; }
    public DeferredTaxCheck? Deferred { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (await LoadPageAsync() is { } refusal)
            return refusal;
        Entry.Date = YearEnd.ToDateTime(TimeOnly.MinValue) < DateTime.Today ? YearEnd.ToDateTime(TimeOnly.MinValue) : DateTime.Today;
        Entry.TaxRate = Workpaper!.Tax?.RatePercent ?? Entry.TaxRate;
        return Page();
    }

    public async Task<IActionResult> OnPostAddAsync()
    {
        if (await LoadPageAsync() is { } refusal)
            return refusal;
        return Done(await franking.AddAsync(Portfolio!.PortfolioId, Entry), "Franking account entry added.");
    }

    public async Task<IActionResult> OnPostDeleteAsync(int entryId)
    {
        if (await LoadPageAsync() is { } refusal)
            return refusal;
        return Done(await franking.DeleteAsync(Portfolio!.PortfolioId, entryId), "Franking account entry deleted.");
    }

    private async Task<IActionResult?> LoadPageAsync()
    {
        if (await LoadAsync() is { } refusal)
            return refusal;
        if (Portfolio is null)
            return null;
        Franking = await franking.GetLedgerAsync(Portfolio.PortfolioId, Workpaper!.YearStart, YearEnd);
        Deferred = DeferredTaxCheck.From(Workpaper.Ledger, Workpaper.Tax?.RatePercent ?? Core.TaxWork.TaxCalculation.DefaultCompanyRate);
        return null;
    }
}
