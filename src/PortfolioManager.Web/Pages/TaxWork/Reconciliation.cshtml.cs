using Microsoft.AspNetCore.Mvc;
using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Clients;
using PortfolioManager.Core.TaxWork;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.TaxWork;

/// <summary>Income tax workpaper: accounting profit to taxable income, and the year's settings and adjustments.</summary>
public class ReconciliationModel(CurrentPortfolio current, TaxWorkpaperService workpapers, LockedPeriodService locks, TaxYearService years)
    : TaxYearPageModel(current, workpapers, locks)
{
    [BindProperty]
    public TaxYearInput Settings { get; set; } = new();

    [BindProperty]
    public TaxAdjustmentInput Adjustment { get; set; } = new();

    public async Task<IActionResult> OnGetAsync()
    {
        if (await LoadAsync() is { } refusal)
            return refusal;
        if (Workpaper is not null)
            Settings = TaxYearInput.From(Workpaper.Data.Settings);
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        if (await LoadAsync() is { } refusal)
            return refusal;
        // Fields the entity type doesn't show keep what was saved
        var saved = Workpaper!.Data.Settings;
        var type = Workpaper.EntityType;
        Settings.ResolutionDate = saved.ResolutionDate?.ToDateTime(TimeOnly.MinValue);
        if (type != ClientEntityTypes.Company)
            Settings.TaxRatePercent = saved.TaxRatePercent;
        if (type != ClientEntityTypes.Smsf)
            Settings.EcpiPercent = saved.EcpiPercent;
        if (type is not (ClientEntityTypes.Company or ClientEntityTypes.Smsf))
            Settings.PaygInstalments = saved.PaygInstalments;
        return Done(await years.SaveAsync(Portfolio!.PortfolioId, Year, YearEnd, Settings), "Tax settings saved.");
    }

    public async Task<IActionResult> OnPostAddAsync()
    {
        if (await LoadAsync() is { } refusal)
            return refusal;
        return Done(await years.AddAdjustmentAsync(Portfolio!.PortfolioId, Year, YearEnd, Adjustment), "Adjustment added.");
    }

    public async Task<IActionResult> OnPostDeleteAsync(int adjustmentId)
    {
        if (await LoadAsync() is { } refusal)
            return refusal;
        return Done(await years.DeleteAdjustmentAsync(Portfolio!.PortfolioId, Year, YearEnd, adjustmentId), "Adjustment deleted.");
    }
}
