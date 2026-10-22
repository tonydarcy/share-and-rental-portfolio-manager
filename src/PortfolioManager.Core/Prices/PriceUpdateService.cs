namespace PortfolioManager.Core.Prices;

public sealed record SymbolUpdate(PriceSymbol Symbol, PriceFetchStatus Status, int PricesSaved, DateOnly? LatestDate, string? Message)
{
    public bool Succeeded => Status == PriceFetchStatus.Ok && Message is null;
}

public sealed record PriceUpdateSummary(
    DateOnly From, DateOnly To,
    IReadOnlyList<SymbolUpdate> Symbols,
    IReadOnlyList<string> TrendErrors)
{
    public int Succeeded => Symbols.Count(s => s.Succeeded);
    public int Failed => Symbols.Count - Succeeded;
    public int PricesSaved => Symbols.Sum(s => s.PricesSaved);
    public bool HasErrors => Failed > 0 || TrendErrors.Count > 0;
}

/// <summary>
/// The daily price run: fetch recent end-of-day prices for every held stock and FX pair,
/// save them through uspUploadSharePrice, then update trends per exchange, as the legacy upload does.
/// A failing symbol is logged and skipped; only a bad key or a used-up daily allowance stops the fetching.
/// </summary>
public sealed class PriceUpdateService(IPriceStore store, IPriceProvider provider)
{
    public async Task<PriceUpdateSummary> RunAsync(DateOnly from, DateOnly to, Action<string>? log = null, CancellationToken ct = default)
    {
        log ??= _ => { };
        var symbols = await store.GetSymbolsToUpdateAsync(ct);
        log($"{symbols.Count} symbols to update from {provider.Name}, {from:yyyy-MM-dd} to {to:yyyy-MM-dd}.");

        var results = new List<SymbolUpdate>();
        var latest = new Dictionary<PriceSymbol, DailyPrice>();
        string? stopReason = null;

        foreach (var symbol in symbols)
        {
            if (stopReason is not null)
            {
                results.Add(new SymbolUpdate(symbol, PriceFetchStatus.Failed, 0, null, $"Skipped: {stopReason}"));
                continue;
            }

            var fetched = await provider.GetDailyPricesAsync(symbol, from, to, ct);
            if (!fetched.IsOk)
            {
                results.Add(new SymbolUpdate(symbol, fetched.Status, 0, null, fetched.Message));
                log($"{symbol}: {fetched.Status} - {fetched.Message}");
                if (fetched.StopsRun)
                    stopReason = fetched.Message ?? fetched.Status.ToString();
                continue;
            }

            // Oldest first: uspUploadSharePrice keeps only the newest row of each symbol in tblSharePriceCache
            var saved = 0;
            string? error = null;
            foreach (var price in fetched.Prices.OrderBy(p => p.Date))
            {
                try
                {
                    await store.UploadAsync(symbol, price, ct);
                    saved++;
                    latest[symbol] = price;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    error = $"Saving {price.Date:yyyy-MM-dd} failed: {ex.Message}";
                    break;
                }
            }

            var update = new SymbolUpdate(symbol, PriceFetchStatus.Ok, saved,
                latest.TryGetValue(symbol, out var last) ? last.Date : null,
                error ?? (fetched.Prices.Count == 0 ? "No prices returned for these dates." : null));
            results.Add(update);
            log(update.Message is null
                ? $"{symbol}: saved {saved} price(s), latest {update.LatestDate:yyyy-MM-dd} close {last!.Close}"
                : $"{symbol}: saved {saved} price(s). {update.Message}");
        }

        var trendErrors = new List<string>();
        foreach (var exchange in latest.Keys.Select(s => s.ExchangeCode).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var passes = await store.UpdateTrendsAsync(exchange, ct);
                log($"Trends updated for {exchange} ({passes} pass(es)).");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                trendErrors.Add($"{exchange}: {ex.Message}");
                log($"Trend update for {exchange} failed: {ex.Message}");
            }
        }

        // The trend proc's cache clean-up keeps FOREX rows only when coded like 'USDAUD', so a
        // 'USD/AUD' rate it just removed is put back by saving the newest rate once more.
        foreach (var (symbol, price) in latest.Where(l => l.Key.IsForex))
        {
            try { await store.UploadAsync(symbol, price, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                trendErrors.Add($"{symbol}: re-saving the latest rate failed: {ex.Message}");
            }
        }

        var summary = new PriceUpdateSummary(from, to, results, trendErrors);
        log($"Done: {summary.Succeeded} of {results.Count} symbols updated, {summary.PricesSaved} prices saved, {summary.Failed} failed.");
        foreach (var failed in results.Where(r => !r.Succeeded))
            log($"  Not updated: {failed.Symbol} ({failed.Status}) {failed.Message}");
        return summary;
    }
}
