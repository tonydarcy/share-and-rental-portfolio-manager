using PortfolioManager.Core.TaxWork;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Rentals;

/// <summary>Each property's schedule this year against last year, flagging big changes for review.</summary>
public class CompareModel(CurrentPortfolio current, RentalService rentals) : RentalYearPageModel(current, rentals)
{
    public IReadOnlyList<(RentalProperty Property, IReadOnlyList<RentalComparisonLine> Lines)> Properties { get; private set; } = [];

    public async Task OnGetAsync()
    {
        if (!await LoadAsync(allProperties: true))
            return;
        var previous = (await Rentals.GetYearAsync(Portfolio!.PortfolioId, Year - 1, allProperties: true)).Properties.ToDictionary(p => p.Property.Id);
        Properties = Schedule.Properties
            .Where(p => previous.ContainsKey(p.Property.Id))
            .Select(p => (p.Property, RentalComparison.Compare(previous[p.Property.Id], p)))
            .Where(p => p.Item2.Count > 1 || p.Item2.Any(l => l.Current != 0 || l.Previous != 0))
            .ToList();
    }
}
