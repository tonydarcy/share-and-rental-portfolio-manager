using PortfolioManager.Core.Rentals;
using PortfolioManager.Core.Tax;
using PortfolioManager.Core.TaxWork;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Rentals;

/// <summary>Things on each property's schedule worth checking before the return goes in.</summary>
public class ReviewModel(CurrentPortfolio current, RentalService rentals, PropertyService book) : RentalYearPageModel(current, rentals)
{
    public IReadOnlyList<RentalFlag> Flags { get; private set; } = [];

    public async Task OnGetAsync()
    {
        if (!await LoadAsync())
            return;
        Flags = await FlagsAsync(Rentals, book, Portfolio!.PortfolioId, Schedule, Today);
    }

    /// <summary>The review flags for every property in the schedule (also shown on the Properties page).</summary>
    public static async Task<IReadOnlyList<RentalFlag>> FlagsAsync(RentalService rentals, PropertyService book, int portfolioId, RentalSchedule schedule, DateOnly today)
    {
        if (schedule.Properties.Count == 0)
            return [];
        var from = FinancialYear.StartDate(schedule.Year);
        var to = FinancialYear.EndDate(schedule.Year);
        var dated = await rentals.GetDatedAsync(portfolioId, schedule.Properties.Select(p => p.Property).ToList(), from, to);
        var jobs = (await book.GetAsync(portfolioId)).Jobs.Where(j => j.Treatment == JobTreatments.Repair && j.Done >= from && j.Done <= to).ToList();
        return schedule.Properties.SelectMany(p => RentalReview.Check(p,
                dated.Where(d => d.PropertyId == p.Property.Id && d.Source == "Agent statement").Select(d => d.Date).Distinct().ToList(),
                jobs.Where(j => j.PropertyId == p.Property.Id).Select(j => j.Cost), today))
            .ToList();
    }
}
