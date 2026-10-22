namespace PortfolioManager.Core.Prices;

/// <summary>A stock or FX pair as the database names it, e.g. ASX:BHP or FOREX:USD/AUD.</summary>
public sealed record PriceSymbol(string ExchangeCode, string StockCode)
{
    public const string ForexExchange = "FOREX";

    public bool IsForex => string.Equals(ExchangeCode, ForexExchange, StringComparison.OrdinalIgnoreCase);

    /// <summary>FX pairs are stored like the legacy price files: 'USD/AUD' = AUD per 1 USD.</summary>
    public static PriceSymbol Forex(string fromCurrency, string toCurrency) => new(ForexExchange, $"{fromCurrency}/{toCurrency}");

    public override string ToString() => $"{ExchangeCode}:{StockCode}";
}

public sealed record DailyPrice(DateOnly Date, double? Open, double? High, double? Low, double Close, double? Volume);

public enum PriceFetchStatus
{
    Ok,
    /// <summary>The app's exchange code has no mapping for this provider.</summary>
    NotSupported,
    NotFound,
    /// <summary>The key is valid but the plan doesn't cover this symbol.</summary>
    NotEntitled,
    RateLimited,
    /// <summary>The plan's daily allowance is used up: no point asking for more today.</summary>
    QuotaExceeded,
    /// <summary>Missing or wrong API key: no point asking for more.</summary>
    Unauthorized,
    Failed,
}

public sealed record PriceFetchResult(PriceFetchStatus Status, IReadOnlyList<DailyPrice> Prices, string? Message = null, TimeSpan? RetryAfter = null)
{
    public bool IsOk => Status == PriceFetchStatus.Ok;

    /// <summary>True when every later request in this run would fail the same way.</summary>
    public bool StopsRun => Status is PriceFetchStatus.QuotaExceeded or PriceFetchStatus.Unauthorized;

    public static PriceFetchResult Ok(IReadOnlyList<DailyPrice> prices) => new(PriceFetchStatus.Ok, prices);
    public static PriceFetchResult Fail(PriceFetchStatus status, string message, TimeSpan? retryAfter = null) => new(status, [], message, retryAfter);
}

/// <summary>
/// A source of end-of-day prices. Each instance carries its own settings (API key etc.),
/// so a per-user key later only means creating a provider with that user's settings.
/// </summary>
public interface IPriceProvider
{
    string Name { get; }

    /// <summary>Daily prices from <paramref name="from"/> to <paramref name="to"/> inclusive, oldest first.</summary>
    Task<PriceFetchResult> GetDailyPricesAsync(PriceSymbol symbol, DateOnly from, DateOnly to, CancellationToken ct = default);
}
