using System.Data;
using System.Text.RegularExpressions;
using Dapper;
using PortfolioManager.Core.Data;

namespace PortfolioManager.Core.Prices;

public interface IPriceStore
{
    /// <summary>Every stock held in any portfolio, plus the FX pairs those portfolios need.</summary>
    Task<IReadOnlyList<PriceSymbol>> GetSymbolsToUpdateAsync(CancellationToken ct = default);

    /// <summary>Saves one day's price the way the legacy price file upload does.</summary>
    Task UploadAsync(PriceSymbol symbol, DailyPrice price, CancellationToken ct = default);

    /// <summary>Works out the 200-day average / trend for new prices on one exchange, like the legacy upload. Returns the number of passes.</summary>
    Task<int> UpdateTrendsAsync(string exchangeCode, CancellationToken ct = default);
}

/// <param name="alwaysUpdate">Extra symbols fetched even when nobody holds them, such as a benchmark ETF.</param>
public sealed partial class PriceStore(IDbConnectionFactory db, IEnumerable<PriceSymbol>? alwaysUpdate = null) : IPriceStore
{
    public const string TrendsDone = "Command Completed Sucessfully"; // spelt as the proc returns it
    public const int MaxTrendPasses = 50;

    public async Task<IReadOnlyList<PriceSymbol>> GetSymbolsToUpdateAsync(CancellationToken ct = default)
    {
        await using var con = db.Create();

        // Same holding sum as the Holdings procs: Buy quantity less Sell quantity
        var stocks = await con.QueryAsync<(string Exchange, string Code)>(new CommandDefinition(
            "SELECT strStockExchangeCode, strStockCode FROM tblShareTrades " +
            "GROUP BY strStockExchangeCode, strStockCode " +
            "HAVING SUM(CASE WHEN UPPER(strType) = 'BUY' THEN iQuantity ELSE 0 END) " +
            "     - SUM(CASE WHEN UPPER(strType) = 'SELL' THEN iQuantity ELSE 0 END) > 0.0001",
            cancellationToken: ct));

        // Currencies of trades and cash accounts against each portfolio's own currency
        var currencies = await con.QueryAsync<(string Currency, string PortfolioCurrency)>(new CommandDefinition(
            "SELECT DISTINCT C.strCurrency, U.strPortfolioCurrency FROM (" +
            "  SELECT iUserId, strCurrency FROM tblShareTrades UNION SELECT iUserId, strCurrency FROM tblBankStatement" +
            ") C INNER JOIN tblUsers U ON U.iUserId = C.iUserId",
            cancellationToken: ct));

        return stocks.Select(s => new PriceSymbol(s.Exchange, s.Code))
            .Concat(alwaysUpdate ?? [])
            .Where(s => !s.IsForex && !string.IsNullOrWhiteSpace(s.ExchangeCode) && !string.IsNullOrWhiteSpace(s.StockCode))
            .Where(s => !AssetTypes.IsManual(s.ExchangeCode)) // priced by hand
            .Distinct()
            .OrderBy(s => s.ExchangeCode).ThenBy(s => s.StockCode)
            .Concat(ForexSymbols(currencies))
            .ToList();
    }

    /// <summary>Reads "ASX:STW" style settings, skipping anything without both parts.</summary>
    public static IReadOnlyList<PriceSymbol> ParseSymbols(IEnumerable<string>? settings) =>
        (settings ?? [])
            .Select(s => (s ?? "").Split(':', 2, StringSplitOptions.TrimEntries))
            .Where(p => p.Length == 2 && p[0].Length > 0 && p[1].Length > 0)
            .Select(p => new PriceSymbol(p[0].ToUpperInvariant(), p[1].ToUpperInvariant()))
            .Distinct()
            .ToList();

    /// <summary>One 'USD/AUD' style pair per foreign currency, skipping blanks and anything that isn't a 3-letter code.</summary>
    public static IReadOnlyList<PriceSymbol> ForexSymbols(IEnumerable<(string Currency, string PortfolioCurrency)> pairs) =>
        pairs
            .Select(p => (From: (p.Currency ?? "").Trim().ToUpperInvariant(), To: (p.PortfolioCurrency ?? "").Trim().ToUpperInvariant()))
            .Where(p => CurrencyCode().IsMatch(p.From) && CurrencyCode().IsMatch(p.To) && p.From != p.To)
            .Distinct()
            .OrderBy(p => p.From).ThenBy(p => p.To)
            .Select(p => PriceSymbol.Forex(p.From, p.To))
            .ToList();

    public async Task UploadAsync(PriceSymbol symbol, DailyPrice price, CancellationToken ct = default)
    {
        await using var con = db.Create();
        await con.ExecuteAsync(new CommandDefinition("uspUploadSharePrice", new
        {
            strStockExchangeCode = symbol.ExchangeCode,
            strStockCode = symbol.StockCode,
            dtmDetailsAsAtDate = price.Date.ToDateTime(TimeOnly.MinValue),
            dblOpenPrice = price.Open ?? 0,
            dblHighPrice = price.High ?? 0,
            dblLowPrice = price.Low ?? 0,
            dblClosePrice = price.Close,
            iVolume = price.Volume ?? 0,
        }, commandType: CommandType.StoredProcedure, cancellationToken: ct));
    }

    public async Task<int> UpdateTrendsAsync(string exchangeCode, CancellationToken ct = default)
    {
        // Default.aspx runs this proc again until it reports it's finished (it does at most 1000 rows a pass)
        for (var pass = 1; pass <= MaxTrendPasses; pass++)
        {
            await using var con = db.Create();
            var result = await con.ExecuteScalarAsync<object?>(new CommandDefinition("uspUpdateMissingSharePriceTrendsByDateAndExchange", new
            {
                strStockExchangeCode = exchangeCode,
                dtmDetailsAsAtDate = (DateTime?)null,
                dtmStartDate = (DateTime?)null,
            }, commandType: CommandType.StoredProcedure, commandTimeout: 600, cancellationToken: ct));

            if (Convert.ToString(result) == TrendsDone)
                return pass;
        }
        throw new InvalidOperationException($"Trend update for {exchangeCode} was still not finished after {MaxTrendPasses} passes.");
    }

    [GeneratedRegex("^[A-Z]{3}$")]
    private static partial Regex CurrencyCode();
}
