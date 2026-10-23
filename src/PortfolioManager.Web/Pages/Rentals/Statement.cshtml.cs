using Microsoft.AspNetCore.Mvc;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Core.Tax;
using PortfolioManager.Core.TaxWork;
using PortfolioManager.Web.Pages.Reports;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Rentals;

/// <summary>A property's income and expenses by month for a financial year or any dates, with a total column.</summary>
public class StatementModel(CurrentPortfolio current, RentalService rentals) : PeriodReportModel
{
    [BindProperty(SupportsGet = true)] public int? Id { get; set; }

    public PortfolioSummary? Portfolio { get; private set; }
    public IReadOnlyList<RentalProperty> Properties { get; private set; } = [];
    public RentalStatement? Report { get; private set; }

    public async Task OnGetAsync()
    {
        ResolvePeriod();
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is null)
            return;
        Properties = await rentals.GetPropertiesAsync(Portfolio.PortfolioId);
        var picked = Properties.Where(p => Id is null || p.Id == Id).ToList();
        if (picked.Count == 0)
            return;
        var dated = await rentals.GetDatedAsync(Portfolio.PortfolioId, picked, Period.From, Period.To);
        // The year's keyed figures have no dates, so they only come in for a whole financial year
        Dictionary<string, decimal>? keyed = null;
        if (Period.FinancialYear is { } fy)
        {
            keyed = [];
            foreach (var py in (await rentals.GetYearAsync(Portfolio.PortfolioId, fy, allProperties: true)).Properties.Where(p => picked.Any(x => x.Id == p.Property.Id)))
                foreach (var (key, amount) in py.Amounts.Where(a => !RentalCategories.Counts.Contains(a.Key)))
                    keyed[key] = keyed.GetValueOrDefault(key) + amount;
        }
        Report = RentalStatements.Build(Period.From, Period.To, dated, keyed);
    }
}
