using Dapper;
using PortfolioManager.Core.Data;

namespace PortfolioManager.Core.Prices;

/// <summary>One day's price with the trend figures the legacy price upload works out (200-day average, 52-week standard deviation).</summary>
public sealed record PriceBar(DateOnly Date, double? Open, double? High, double? Low, double Close, double? Volume, double? Sma200, double? Sd52)
{
    public ShareTrend Trend => ShareTrend.Read(Close, Sma200, Sd52);
}

/// <summary>
/// Bullish when the close is more than one standard deviation above its 200-day average, Bearish when
/// more than one below, otherwise Neutral (the legacy share trends rule). Unknown until the figures exist.
/// </summary>
public sealed record ShareTrend(string Status, double? StandardDeviations)
{
    public const string Bullish = "Bullish", Neutral = "Neutral", Bearish = "Bearish", Unknown = "";

    public static ShareTrend Read(double close, double? sma200, double? sd52)
    {
        if (sma200 is not > 0 || sd52 is not > 0)
            return new(Unknown, null);
        var sds = (close - sma200.Value) / sd52.Value;
        var status = close > sma200 + sd52 ? Bullish : close < sma200 - sd52 ? Bearish : Neutral;
        return new(status, Math.Round(sds, 1));
    }

    public string BadgeClass => Status switch
    {
        Bullish => "text-bg-success",
        Bearish => "text-bg-danger",
        Neutral => "text-bg-secondary",
        _ => "text-bg-light",
    };
}

public sealed record Quote(
    string Exchange,
    string Code,
    string? Name,
    string? Sector,
    IReadOnlyList<PriceBar> Bars,
    double UnitsHeld)
{
    public PriceBar? Latest => Bars.Count > 0 ? Bars[^1] : null;
    public PriceBar? Previous => Bars.Count > 1 ? Bars[^2] : null;

    public double? Change => Latest is { } l && Previous is { } p ? l.Close - p.Close : null;
    public double? ChangePct => Latest is { } l && Previous is { Close: > 0 } p ? l.Close / p.Close - 1 : null;

    /// <summary>Highest and lowest close in the year to the latest price.</summary>
    public (double Low, double High)? YearRange
    {
        get
        {
            if (Latest is not { } l)
                return null;
            var year = Bars.Where(b => b.Date > l.Date.AddYears(-1)).Select(b => b.Close).ToList();
            return (year.Min(), year.Max());
        }
    }
}

/// <summary>A stock's latest trend reading, for the share trends list.</summary>
public sealed record TrendRow(
    string Exchange,
    string Code,
    string? Name,
    DateOnly Date,
    double Close,
    double? WeekAgoClose,
    double? Sma200,
    double? Sd52)
{
    public ShareTrend Trend => ShareTrend.Read(Close, Sma200, Sd52);
    public double? WeekChange => WeekAgoClose is > 0 ? Close / WeekAgoClose.Value - 1 : null;
}

public enum TrendScope { Holdings, Popular, All }

/// <summary>Share quotes and price trends from tblSharePrice (the legacy Get Share Price and Share Trends pages).</summary>
public sealed class QuoteService(IDbConnectionFactory db)
{
    /// <summary>The legacy site's list of popular ASX companies in tblShareGroups.</summary>
    public const string PopularGroup = "Selected Popular ASX Companies";

    private sealed record BarRow(DateTime Date, double? Open, double? High, double? Low, double Close, double? Volume, double? Sma200, double? Sd52);

    private sealed record TrendDbRow(string Exchange, string Code, string? Name, DateTime Date, double Close, double? WeekAgoClose, double? Sma200, double? Sd52);

    /// <summary>
    /// Up to the last 1,000 prices for a stock, oldest first, plus its name and the units this portfolio holds.
    /// Prices a portfolio uploaded for itself (iUserID set) are only seen by that portfolio.
    /// </summary>
    public async Task<Quote> GetQuoteAsync(int portfolioId, string exchange, string code)
    {
        var p = new { portfolioId, exchange = AnsiText.Of(exchange), code = AnsiText.Of(code) };
        await using var con = db.Create();
        using var grid = await con.QueryMultipleAsync("""
            SELECT TOP 1000 CAST(dtmDetailsAsAtDate AS date) AS Date, dblOpenPrice AS [Open], dblHighPrice AS High, dblLowPrice AS Low,
                   dblClosePrice AS [Close], iVolume AS Volume, dblClosePrice200DayAverage AS Sma200, dblStandardDeviation52Week AS Sd52
            FROM tblSharePrice
            WHERE strStockExchangeCode = @exchange AND strStockCode = @code AND dblClosePrice > 0
              AND (iUserID IS NULL OR iUserID = 0 OR iUserID = @portfolioId)
            ORDER BY dtmDetailsAsAtDate DESC;

            SELECT TOP 1 strStockName, strSector FROM tblShareDetails
            WHERE strStockExchangeCode = @exchange AND strStockCode = @code;

            SELECT ISNULL(SUM(CASE WHEN UPPER(strType) = 'BUY' THEN iQuantity WHEN UPPER(strType) = 'SELL' THEN -iQuantity ELSE 0 END), 0)
            FROM tblShareTrades
            WHERE iUserId = @portfolioId AND strStockExchangeCode = @exchange AND strStockCode = @code;
            """, p);

        var bars = (await grid.ReadAsync<BarRow>())
            .Select(b => new PriceBar(DateOnly.FromDateTime(b.Date), b.Open, b.High, b.Low, b.Close, b.Volume, b.Sma200, b.Sd52))
            .GroupBy(b => b.Date).Select(g => g.First()) // a duplicate upload for the same day
            .OrderBy(b => b.Date)
            .ToList();
        var details = (await grid.ReadAsync<(string? Name, string? Sector)>()).FirstOrDefault();
        var units = await grid.ReadSingleAsync<double>();

        return new Quote(exchange, code, Clean(details.Name), Clean(details.Sector), bars, Math.Round(units, 6));
    }

    /// <summary>Stocks this portfolio has traded, as "ASX:CBA", for the quote search box.</summary>
    public async Task<IReadOnlyList<string>> GetTradedSymbolsAsync(int portfolioId)
    {
        await using var con = db.Create();
        var rows = await con.QueryAsync<(string Exchange, string Code)>(
            "SELECT DISTINCT strStockExchangeCode, strStockCode FROM tblShareTrades WHERE iUserId = @portfolioId",
            new { portfolioId });
        return rows.Select(r => $"{r.Exchange.Trim()}:{r.Code.Trim()}").Distinct().Order().ToList();
    }

    /// <summary>Each stock's latest price in the last 60 days that has trend figures, strongest trend first.</summary>
    public async Task<IReadOnlyList<TrendRow>> GetTrendsAsync(int portfolioId, TrendScope scope)
    {
        await using var con = db.Create();
        var rows = await con.QueryAsync<TrendDbRow>("""
            WITH Picked AS (
                SELECT P.strStockExchangeCode, P.strStockCode, P.dtmDetailsAsAtDate, P.dblClosePrice,
                       P.dblClosePrice200DayAverage, P.dblStandardDeviation52Week,
                       ROW_NUMBER() OVER (PARTITION BY P.strStockExchangeCode, P.strStockCode ORDER BY P.dtmDetailsAsAtDate DESC) AS rn
                FROM tblSharePrice P
                WHERE P.dtmDetailsAsAtDate >= DATEADD(DAY, -60, GETDATE())
                  AND P.dblClosePrice > 0 AND P.dblStandardDeviation52Week > 0 AND P.dblClosePrice200DayAverage > 0
                  AND P.strStockExchangeCode <> 'FOREX'
                  AND (P.iUserID IS NULL OR P.iUserID = 0 OR P.iUserID = @portfolioId)
                  AND (@scope = 'All'
                       OR (@scope = 'Popular' AND EXISTS (
                            SELECT 1 FROM tblShareGroups G
                            WHERE G.strGroupName = @popular AND G.dtmDateRemoved IS NULL
                              AND G.strStockExchangeCode = P.strStockExchangeCode AND G.strStockCode = P.strStockCode))
                       OR (@scope = 'Holdings' AND EXISTS (
                            SELECT 1 FROM tblShareTrades T
                            WHERE T.iUserId = @portfolioId
                              AND T.strStockExchangeCode = P.strStockExchangeCode AND T.strStockCode = P.strStockCode
                            GROUP BY T.strStockExchangeCode, T.strStockCode
                            HAVING SUM(CASE WHEN UPPER(T.strType) = 'BUY' THEN T.iQuantity WHEN UPPER(T.strType) = 'SELL' THEN -T.iQuantity ELSE 0 END) > 0.0001)))
            )
            SELECT L.strStockExchangeCode AS Exchange, L.strStockCode AS Code,
                   (SELECT TOP 1 D.strStockName FROM tblShareDetails D
                    WHERE D.strStockExchangeCode = L.strStockExchangeCode AND D.strStockCode = L.strStockCode) AS Name,
                   CAST(L.dtmDetailsAsAtDate AS date) AS Date, L.dblClosePrice AS [Close],
                   (SELECT TOP 1 W.dblClosePrice FROM tblSharePrice W
                    WHERE W.strStockExchangeCode = L.strStockExchangeCode AND W.strStockCode = L.strStockCode
                      AND W.dblClosePrice > 0 AND W.dtmDetailsAsAtDate <= DATEADD(DAY, -7, L.dtmDetailsAsAtDate)
                      AND (W.iUserID IS NULL OR W.iUserID = 0 OR W.iUserID = @portfolioId)
                    ORDER BY W.dtmDetailsAsAtDate DESC) AS WeekAgoClose,
                   L.dblClosePrice200DayAverage AS Sma200, L.dblStandardDeviation52Week AS Sd52
            FROM Picked L
            WHERE L.rn = 1
            """, new { portfolioId, scope = scope.ToString(), popular = PopularGroup }, commandTimeout: 120);

        return rows
            .Select(r => new TrendRow(r.Exchange.Trim(), r.Code.Trim(), Clean(r.Name), DateOnly.FromDateTime(r.Date), r.Close, r.WeekAgoClose, r.Sma200, r.Sd52))
            .OrderByDescending(r => r.Trend.StandardDeviations ?? double.MinValue)
            .ThenBy(r => r.Exchange).ThenBy(r => r.Code)
            .ToList();
    }

    /// <summary>Reads "ASX:CBA" or a bare code (taken as ASX). Null for anything blank or odd.</summary>
    public static (string Exchange, string Code)? ParseSymbol(string? text)
    {
        var t = (text ?? "").Trim().ToUpperInvariant();
        if (t.Length is 0 or > 101)
            return null;
        var parts = t.Split(':', 2, StringSplitOptions.TrimEntries);
        var (exchange, code) = parts.Length == 2 ? (parts[0], parts[1]) : ("ASX", parts[0]);
        if (exchange.Length is 0 or > 50 || code.Length is 0 or > 50)
            return null;
        if (!code.All(ch => char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_' or '/') || !exchange.All(char.IsLetterOrDigit))
            return null;
        return (exchange, code);
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
