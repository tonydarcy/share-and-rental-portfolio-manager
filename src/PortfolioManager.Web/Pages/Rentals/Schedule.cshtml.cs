using PortfolioManager.Core.TaxWork;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Rentals;

/// <summary>The ATO rental property schedule for each property, with a column for each owner, to print for the client file.</summary>
public class ScheduleModel(CurrentPortfolio current, RentalService rentals) : RentalYearPageModel(current, rentals)
{
    public async Task OnGetAsync() => await LoadAsync();
}
