using PortfolioManager.Core.Debt;
using PortfolioManager.Core.Rentals;
using PortfolioManager.Core.Tax;
using PortfolioManager.Core.TaxWork;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Rentals;

/// <summary>Every rental property: value, loans, equity and yields, who is renting it, and what needs doing.</summary>
public class IndexModel(CurrentPortfolio current, RentalService rentals, PropertyService book, LoanService loans) : RentalYearPageModel(current, rentals)
{
    public IReadOnlyList<RentalYield> Yields { get; private set; } = [];
    public IReadOnlyList<PropertyReminder> Reminders { get; private set; } = [];
    public IReadOnlyList<RentPosition> Rents { get; private set; } = [];
    public int FlagCount { get; private set; }

    public decimal TotalEquity => Yields.Sum(y => y.Equity);

    public async Task OnGetAsync()
    {
        if (!await LoadAsync(allProperties: true))
            return;
        var id = Portfolio!.PortfolioId;
        Yields = await YieldsAsync(Rentals, loans, id, Schedule);
        var b = await book.GetAsync(id);
        var properties = Schedule.Properties.Select(p => p.Property).ToList();
        Reminders = PropertyReminders.Due(properties.Where(p => p.Active), b.Tenancies, b.Payments, b.Compliance, Today);
        Rents = b.Tenancies.Where(t => t.Current(Today)).Select(t => RentLedger.Position(t, b.Payments, Today)).ToList();
        FlagCount = (await ReviewModel.FlagsAsync(Rentals, book, id, new RentalSchedule(Year, Schedule.Properties.Where(p => p.Property.Active || p.Amounts.Count > 0).ToList()), Today)).Count;
    }

    /// <summary>Each property's value, linked loans owed and the year's rent and cash expenses (also used by the dashboard).</summary>
    public static async Task<IReadOnlyList<RentalYield>> YieldsAsync(RentalService rentals, LoanService loans, int portfolioId, RentalSchedule schedule)
    {
        if (schedule.Properties.Count == 0)
            return [];
        var links = await rentals.GetLoanLinksAsync(portfolioId);
        var (all, entries) = links.Count > 0 ? await loans.GetAsync(portfolioId) : ([], []);
        var today = DateOnly.FromDateTime(DateTime.Today);
        var owed = all.Where(l => links.ContainsKey(l.Id))
            .GroupBy(l => links[l.Id])
            .ToDictionary(g => g.Key, g => g.Sum(l => LoanCalc.Year(l, entries, DateOnly.MinValue, today).ClosingBalance));
        return schedule.Properties
            .Select(p => new RentalYield(p.Property, p.Property.Value, owed.GetValueOrDefault(p.Property.Id), p.WholeIncome, p.WholeCashExpenses))
            .ToList();
    }
}
