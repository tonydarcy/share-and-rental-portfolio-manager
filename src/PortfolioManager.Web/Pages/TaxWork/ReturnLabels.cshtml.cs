using Microsoft.AspNetCore.Mvc;
using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.TaxWork;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.TaxWork;

/// <summary>The tax return figures label by label for the entity type, printable or as a file (?handler=Download&amp;format=labels-csv).</summary>
public class ReturnLabelsModel(CurrentPortfolio current, TaxWorkpaperService workpapers, LockedPeriodService locks)
    : TaxYearPageModel(current, workpapers, locks)
{
    public TaxReturnSchedule? Schedule { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (await LoadAsync() is { } refusal)
            return refusal;
        if (Workpaper is not null)
            Schedule = TaxReturnSchedule.For(Workpaper);
        return Page();
    }

    public async Task<IActionResult> OnGetDownloadAsync(string? format)
    {
        if (await LoadAsync() is { } refusal)
            return refusal;
        if (Workpaper is null || TaxReturnExports.Find(format ?? "labels-csv") is not { } export)
            return NotFound();
        var schedule = TaxReturnSchedule.For(Workpaper);
        return File(export.Write(schedule), export.ContentType, export.FileName(schedule));
    }
}
