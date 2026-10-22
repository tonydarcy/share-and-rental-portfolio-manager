using System.Data;
using Dapper;
using PortfolioManager.Core.Data;

namespace PortfolioManager.Core.Prices;

public interface IAnnouncementStore
{
    /// <summary>Every stock held in any portfolio (no FX pairs).</summary>
    Task<IReadOnlyList<PriceSymbol>> GetHeldStocksAsync(CancellationToken ct = default);

    Task SaveDividendAsync(PriceSymbol symbol, AnnouncedDividend dividend, string source, CancellationToken ct = default);

    Task SaveSplitAsync(PriceSymbol symbol, AnnouncedSplit split, string source, CancellationToken ct = default);
}

/// <summary>tblAnnouncedDividends and tblAnnouncedSplits, through the procs in 2026-10-21-02-announced-actions.sql.</summary>
public sealed class AnnouncementStore(IDbConnectionFactory db) : IAnnouncementStore
{
    public async Task<IReadOnlyList<PriceSymbol>> GetHeldStocksAsync(CancellationToken ct = default)
    {
        await using var con = db.Create();
        // Same holding sum as PriceStore: Buy quantity less Sell quantity
        var stocks = await con.QueryAsync<(string Exchange, string Code)>(new CommandDefinition(
            "SELECT strStockExchangeCode, strStockCode FROM tblShareTrades " +
            "WHERE strStockExchangeCode <> 'FOREX' " +
            "GROUP BY strStockExchangeCode, strStockCode " +
            "HAVING SUM(CASE WHEN UPPER(strType) = 'BUY' THEN iQuantity ELSE 0 END) " +
            "     - SUM(CASE WHEN UPPER(strType) = 'SELL' THEN iQuantity ELSE 0 END) > 0.0001 " +
            "ORDER BY strStockExchangeCode, strStockCode",
            cancellationToken: ct));
        return stocks.Select(s => new PriceSymbol(s.Exchange.Trim().ToUpperInvariant(), s.Code.Trim().ToUpperInvariant()))
            .Where(s => !AssetTypes.IsManual(s.ExchangeCode) && s.ExchangeCode != "CRYPTO")
            .Distinct().ToList();
    }

    public async Task SaveDividendAsync(PriceSymbol symbol, AnnouncedDividend d, string source, CancellationToken ct = default)
    {
        await using var con = db.Create();
        await con.ExecuteAsync(new CommandDefinition("uspSaveAnnouncedDividend", new
        {
            strStockExchangeCode = symbol.ExchangeCode,
            strStockCode = symbol.StockCode,
            dtmExDate = d.ExDate.ToDateTime(TimeOnly.MinValue),
            dtmRecordDate = d.RecordDate?.ToDateTime(TimeOnly.MinValue),
            dtmPaymentDate = d.PaymentDate?.ToDateTime(TimeOnly.MinValue),
            dtmDeclarationDate = d.DeclarationDate?.ToDateTime(TimeOnly.MinValue),
            dblAmountPerUnit = (double)d.AmountPerUnit,
            strCurrency = d.Currency,
            strPeriod = d.Period is { Length: > 50 } p ? p[..50] : d.Period,
            strSource = source,
        }, commandType: CommandType.StoredProcedure, cancellationToken: ct));
    }

    public async Task SaveSplitAsync(PriceSymbol symbol, AnnouncedSplit s, string source, CancellationToken ct = default)
    {
        await using var con = db.Create();
        await con.ExecuteAsync(new CommandDefinition("uspSaveAnnouncedSplit", new
        {
            strStockExchangeCode = symbol.ExchangeCode,
            strStockCode = symbol.StockCode,
            dtmEffectiveDate = s.EffectiveDate.ToDateTime(TimeOnly.MinValue),
            iNewUnits = s.NewUnits,
            iOldUnits = s.OldUnits,
            strSource = source,
        }, commandType: CommandType.StoredProcedure, cancellationToken: ct));
    }
}

public sealed record AnnouncementUpdateSummary(int Symbols, int Dividends, int Splits, IReadOnlyList<string> Errors)
{
    public bool HasErrors => Errors.Count > 0;
}

/// <summary>
/// The daily announcements run: for every held stock, fetch dividends and splits dated from <c>from</c>
/// and save them. Each portfolio then sees them on Corporate actions > Announced, to confirm or dismiss.
/// </summary>
public sealed class AnnouncementUpdateService(IAnnouncementStore store, IAnnouncementProvider provider)
{
    public async Task<AnnouncementUpdateSummary> RunAsync(DateOnly from, Action<string>? log = null, CancellationToken ct = default)
    {
        log ??= _ => { };
        var symbols = await store.GetHeldStocksAsync(ct);
        log($"{symbols.Count} stocks to check for dividends and splits from {provider.Name}, since {from:yyyy-MM-dd}.");

        var errors = new List<string>();
        int dividends = 0, splits = 0;
        foreach (var symbol in symbols)
        {
            var divs = await provider.GetDividendsAsync(symbol, from, ct);
            if (!divs.IsOk)
            {
                errors.Add($"{symbol} dividends: {divs.Message}");
                if (divs.StopsRun) break;
            }
            else
                dividends += await SaveAllAsync(symbol, divs.Items, (d, c) => store.SaveDividendAsync(symbol, d, provider.Name, c), "dividend", errors, ct);

            var sp = await provider.GetSplitsAsync(symbol, from, ct);
            if (!sp.IsOk)
            {
                errors.Add($"{symbol} splits: {sp.Message}");
                if (sp.StopsRun) break;
            }
            else
                splits += await SaveAllAsync(symbol, sp.Items, (s, c) => store.SaveSplitAsync(symbol, s, provider.Name, c), "split", errors, ct);
        }

        foreach (var error in errors)
            log(error);
        log($"Saved {dividends} dividend(s) and {splits} split(s).");
        return new AnnouncementUpdateSummary(symbols.Count, dividends, splits, errors);
    }

    private static async Task<int> SaveAllAsync<T>(PriceSymbol symbol, IReadOnlyList<T> items, Func<T, CancellationToken, Task> save,
        string what, List<string> errors, CancellationToken ct)
    {
        var saved = 0;
        foreach (var item in items)
        {
            try
            {
                await save(item, ct);
                saved++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors.Add($"{symbol}: saving a {what} failed: {ex.Message}");
                break;
            }
        }
        return saved;
    }
}
