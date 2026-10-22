using PortfolioManager.Core.Performance;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Reports;

/// <summary>Which holdings pushed the return up or down over a period, split into price, currency and income.</summary>
public class ContributionModel(CurrentPortfolio current, PerformanceService performance) : PeriodReportModel
{
    public PortfolioSummary? Portfolio { get; private set; }
    public ContributionReport? Report { get; private set; }
    public string? Notice { get; private set; }

    public async Task OnGetAsync()
    {
        ResolvePeriod();
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is null)
            return;
        var to = Period.To > Today ? Today : Period.To;
        if (to < Period.From)
        {
            Notice = "That period hasn't started yet.";
            return;
        }
        Report = await performance.GetContributionAsync(Portfolio.PortfolioId, Period.From, to);
    }

    public static string Pct(double? value) => PerformanceModel.Pct(value);
}
