using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Core.Tax;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Reports;

public class UnrealisedGainsModel(CurrentPortfolio current, UnrealisedGainsService gains, CgtDiscountService cgt) : PageModel
{
    /// <summary>Prices as at this date; today when not given.</summary>
    [BindProperty(SupportsGet = true)]
    public DateOnly? AsAt { get; set; }

    [BindProperty(SupportsGet = true)]
    public bool Group { get; set; }

    public DateOnly AsAtDate => AsAt ?? PeriodReportModel.Today;

    public PortfolioSummary? Portfolio { get; private set; }
    public UnrealisedGainsReport Report { get; private set; } = new([], new(0, 0, 0));
    public ParcelMatch Parcels { get; private set; } = new([], [], []);
    public CgtDiscountRate Rate { get; private set; } = CgtDiscount.RateFor(null);

    /// <summary>The price per unit for each holding, from the report's lines, keyed "exchange:code".</summary>
    public IReadOnlyDictionary<string, decimal> Prices { get; private set; } = new Dictionary<string, decimal>();

    public static string Key(string exchange, string code) => $"{exchange.Trim()}:{code.Trim()}".ToUpperInvariant();

    public async Task OnGetAsync()
    {
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is null)
            return;

        Report = await gains.GetAsync(Portfolio.PortfolioId, AsAtDate, Group);
        Parcels = await cgt.GetOpenParcelsAsync(Portfolio.PortfolioId, AsAtDate);
        Rate = CgtDiscount.RateFor(Portfolio.EntityType);
        Prices = Report.Lines
            .Where(l => l.MarketPrice is not null)
            .GroupBy(l => Key(l.ExchangeCode, l.StockCode))
            .ToDictionary(g => g.Key, g => g.First().MarketPrice!.Value);
    }
}
