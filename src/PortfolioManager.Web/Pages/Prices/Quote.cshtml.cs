using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Core.Prices;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Prices;

public class QuoteModel(CurrentPortfolio current, QuoteService quotes) : PageModel
{
    /// <summary>The stock, as "ASX:CBA" or just "CBA".</summary>
    [BindProperty(SupportsGet = true)]
    public string? Code { get; set; }

    /// <summary>How far back the chart goes: 3m, 1y or all.</summary>
    [BindProperty(SupportsGet = true)]
    public string Range { get; set; } = "1y";

    public PortfolioSummary? Portfolio { get; private set; }
    public IReadOnlyList<string> Symbols { get; private set; } = [];
    public Quote? Quote { get; private set; }
    public string? Error { get; private set; }

    public async Task OnGetAsync()
    {
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is null)
            return;
        Symbols = await quotes.GetTradedSymbolsAsync(Portfolio.PortfolioId);

        if (string.IsNullOrWhiteSpace(Code))
            return;
        if (QuoteService.ParseSymbol(Code) is not { } symbol)
        {
            Error = "Enter a stock code such as CBA or ASX:CBA.";
            return;
        }
        Code = $"{symbol.Exchange}:{symbol.Code}";
        Quote = await quotes.GetQuoteAsync(Portfolio.PortfolioId, symbol.Exchange, symbol.Code);
        if (Quote.Latest is null)
            Error = $"There are no prices on file for {Code}.";
    }

    /// <summary>The bars the chart shows for the chosen range.</summary>
    public IReadOnlyList<PriceBar> ChartBars()
    {
        if (Quote?.Latest is not { } latest)
            return [];
        DateOnly? since = Range switch
        {
            "3m" => latest.Date.AddMonths(-3),
            "all" => null,
            _ => latest.Date.AddYears(-1),
        };
        return Quote.Bars.Where(b => since is null || b.Date > since).ToList();
    }
}
