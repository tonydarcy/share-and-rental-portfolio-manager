using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.TaxWork;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.TaxWork;

/// <summary>The tax return figures laid out to copy onto the ATO's paper form and post, with the address and declaration.</summary>
public class PaperReturnModel(CurrentPortfolio current, TaxWorkpaperService workpapers, LockedPeriodService locks)
    : TaxYearPageModel(current, workpapers, locks)
{
    public TaxReturnSchedule? Schedule { get; private set; }
    public PaperForm? Form { get; private set; }
    public IReadOnlyList<PaperReturnPart> Parts { get; private set; } = [];
    public PaperReturnAddress Address { get; private set; } = PaperReturn.Addresses[0];

    public async Task<Microsoft.AspNetCore.Mvc.IActionResult> OnGetAsync()
    {
        if (await LoadAsync() is { } refusal)
            return refusal;
        if (Workpaper is not null)
        {
            Schedule = TaxReturnSchedule.For(Workpaper);
            Form = PaperReturn.FormFor(Schedule.EntityType);
            Parts = PaperReturn.Parts(Schedule);
            Address = PaperReturn.AddressFor(Workpaper.Client.Address.State);
        }
        return Page();
    }
}
