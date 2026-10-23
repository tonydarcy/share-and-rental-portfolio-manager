using PortfolioManager.Core.TaxWork;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Rentals;

/// <summary>Opening and closing written-down values of each property's assets, the year picked and the years after it.</summary>
public class DepreciationModel(CurrentPortfolio current, RentalService rentals) : RentalYearPageModel(current, rentals)
{
    public const int YearsShown = 5;

    public IReadOnlyList<(RentalProperty Property, IReadOnlyList<DepreciationScheduleRow> Rows)> Properties { get; private set; } = [];

    public async Task OnGetAsync()
    {
        if (!await LoadAsync(allProperties: true))
            return;
        var assets = await Rentals.GetAssetsAsync(Portfolio!.PortfolioId);
        Properties = Schedule.Properties
            .Select(p => (p.Property, RentalDepreciationSchedule.Build(assets.Where(a => a.PropertyId == p.Property.Id), Year, YearsShown)))
            .Where(p => p.Item2.Count > 0)
            .ToList();
    }
}
