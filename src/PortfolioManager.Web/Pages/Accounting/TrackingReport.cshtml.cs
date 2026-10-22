using Microsoft.AspNetCore.Mvc;
using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Tax;
using PortfolioManager.Web.Pages.Shared;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Accounting;

/// <summary>The profit and loss with a column for each option of a tracking category.</summary>
public class TrackingReportModel(CurrentPortfolio current, TrackingService tracking, ILogger<TrackingReportModel> logger) : BooksPageModel(current, logger)
{
    [BindProperty(SupportsGet = true)] public int Position { get; set; } = 1;
    [BindProperty(SupportsGet = true)] public DateTime? From { get; set; }
    [BindProperty(SupportsGet = true)] public DateTime? To { get; set; }

    public TrackingSetup Setup { get; private set; } = TrackingSetup.None;
    public TrackingReport? Report { get; private set; }

    public async Task OnGetAsync()
    {
        if (!await LoadPortfolioAsync())
            return;
        Setup = await tracking.GetAsync(PortfolioId);
        var year = FinancialYear.EndYearFor(Today, Core.Regions.RegionSettings.Current.YearStart);
        From ??= FinancialYear.StartDate(year, Core.Regions.RegionSettings.Current.YearStart).ToDateTime(TimeOnly.MinValue);
        To ??= FinancialYear.EndDate(year, Core.Regions.RegionSettings.Current.YearStart).ToDateTime(TimeOnly.MinValue);
        var category = Setup.Categories.FirstOrDefault(c => c.Position == Position) ?? Setup.Categories.FirstOrDefault();
        if (category is null)
            return;
        Position = category.Position;
        var from = DateOnly.FromDateTime(From.Value);
        var to = DateOnly.FromDateTime(To.Value);
        Report = new TrackingReport(category, from, to, await tracking.GetAmountsAsync(PortfolioId, Position, from, to));
    }
}
