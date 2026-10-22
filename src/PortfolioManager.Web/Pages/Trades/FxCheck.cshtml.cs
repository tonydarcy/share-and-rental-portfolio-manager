using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Cash;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Core.Prices;
using PortfolioManager.Core.Trades;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Trades;

public class FxCheckModel(CurrentPortfolio current, TradesService trades, CashService cash, FxRateService fx) : PageModel
{
    public const int MaxTrades = 1000;

    public PortfolioSummary? Portfolio { get; private set; }
    public string PortfolioCurrency { get; private set; } = "AUD";
    public IReadOnlyList<ForeignTradeCheck> Trades { get; private set; } = [];

    public async Task OnGetAsync()
    {
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is null)
            return;
        PortfolioCurrency = await cash.GetPortfolioCurrencyAsync(Portfolio.PortfolioId);
        var list = new List<ForeignTradeCheck>();
        foreach (var t in (await trades.GetTradesAsync(Portfolio.PortfolioId, null, oldestFirst: false))
                     .Where(t => !string.IsNullOrWhiteSpace(t.Currency) && !t.Currency.Trim().Equals(PortfolioCurrency, StringComparison.OrdinalIgnoreCase))
                     .Take(MaxTrades))
        {
            var date = DateOnly.FromDateTime(t.Date);
            list.Add(new ForeignTradeCheck(t.TradeId, date, t.Type.Trim(), t.ExchangeCode.Trim(), t.StockCode.Trim(), t.Currency.Trim().ToUpperInvariant(),
                t.OrderTotal, (decimal)t.ExchangeRate, await fx.GetAsync(t.Currency, PortfolioCurrency, date)));
        }
        Trades = list;
    }
}
