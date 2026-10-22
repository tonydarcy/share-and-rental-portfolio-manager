using Dapper;
using PortfolioManager.Core.Data;

namespace PortfolioManager.Core.Prices;

/// <summary>A rate in the trades' convention: units of the foreign currency per one unit of the portfolio currency (USD per AUD).</summary>
public sealed record FxRate(string Foreign, string PortfolioCurrency, DateOnly Date, decimal Rate)
{
    /// <summary>Portfolio currency for an amount in the foreign currency, as uspAddShareTrade divides the order total by the rate.</summary>
    public decimal ToPortfolio(decimal foreignAmount) => Math.Round(foreignAmount / Rate, 2, MidpointRounding.AwayFromZero);
}

/// <summary>
/// Exchange rates from the FX prices the price updater saves (tblSharePrice, exchange FOREX). 'USD/AUD' is AUD per 1 USD,
/// so a trade's rate (USD per AUD) is one over its close; 'AUD/USD' is used as it is when that's the pair on file.
/// The rate is the close on the date, or the last one in the week before it (weekends and holidays).
/// </summary>
public sealed class FxRateService(IDbConnectionFactory db)
{
    public const int DaysBack = 7;

    public async Task<FxRate?> GetAsync(string foreign, string portfolioCurrency, DateOnly date)
    {
        foreign = foreign.Trim().ToUpperInvariant();
        portfolioCurrency = portfolioCurrency.Trim().ToUpperInvariant();
        if (foreign == portfolioCurrency)
            return new FxRate(foreign, portfolioCurrency, date, 1m);

        await using var con = db.Create();
        var rows = await con.QueryAsync<(string Pair, DateTime Date, double Close)>("""
            SELECT TOP 2 strStockCode, dtmDetailsAsAtDate, dblClosePrice FROM tblSharePrice
            WHERE strStockExchangeCode = 'FOREX' AND strStockCode IN (@direct, @inverse) AND dblClosePrice > 0
              AND dtmDetailsAsAtDate <= @to AND dtmDetailsAsAtDate >= @from
            ORDER BY dtmDetailsAsAtDate DESC
            """, new
        {
            direct = $"{foreign}/{portfolioCurrency}",
            inverse = $"{portfolioCurrency}/{foreign}",
            to = date.ToDateTime(TimeOnly.MinValue),
            from = date.AddDays(-DaysBack).ToDateTime(TimeOnly.MinValue),
        });
        var row = rows.FirstOrDefault();
        return row.Pair is null ? null : FromPrice(foreign, portfolioCurrency, row.Pair, DateOnly.FromDateTime(row.Date), (decimal)row.Close);
    }

    /// <summary>A stored FX close as a trade rate. Null if the pair isn't one of the two currencies.</summary>
    public static FxRate? FromPrice(string foreign, string portfolioCurrency, string pair, DateOnly date, decimal close)
    {
        if (close <= 0)
            return null;
        if (pair.Equals($"{foreign}/{portfolioCurrency}", StringComparison.OrdinalIgnoreCase))
            return new FxRate(foreign, portfolioCurrency, date, Math.Round(1m / close, 6));
        if (pair.Equals($"{portfolioCurrency}/{foreign}", StringComparison.OrdinalIgnoreCase))
            return new FxRate(foreign, portfolioCurrency, date, Math.Round(close, 6));
        return null;
    }

    /// <summary>True when a trade's rate is more than <paramref name="tolerance"/> away from the market rate (e.g. entered the wrong way up, or left at 1).</summary>
    public static bool LooksWrong(decimal entered, decimal market, decimal tolerance = 0.05m) =>
        entered <= 0 || market <= 0 || Math.Abs(entered - market) / market > tolerance;
}

/// <summary>A trade not in the portfolio currency, with the rate it was entered at and the FX close on file for its date.</summary>
public sealed record ForeignTradeCheck(
    int TradeId, DateOnly Date, string Type, string ExchangeCode, string StockCode, string Currency, decimal OrderTotal,
    decimal EnteredRate, FxRate? Market)
{
    public decimal EnteredTotal => EnteredRate > 0 ? Math.Round(OrderTotal / EnteredRate, 2, MidpointRounding.AwayFromZero) : 0;
    public decimal? MarketTotal => Market?.ToPortfolio(OrderTotal);
    public bool LooksWrong => Market is { } m && FxRateService.LooksWrong(EnteredRate, m.Rate);
}
