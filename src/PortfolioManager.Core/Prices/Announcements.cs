namespace PortfolioManager.Core.Prices;

/// <summary>A dividend as the provider announces it. The amount is per unit as paid, in <see cref="Currency"/>.</summary>
public sealed record AnnouncedDividend(
    DateOnly ExDate, DateOnly? RecordDate, DateOnly? PaymentDate, DateOnly? DeclarationDate,
    decimal AmountPerUnit, string? Currency, string? Period);

/// <summary>A split or consolidation: <see cref="NewUnits"/> for every <see cref="OldUnits"/>, from <see cref="EffectiveDate"/>.</summary>
public sealed record AnnouncedSplit(DateOnly EffectiveDate, int NewUnits, int OldUnits);

public sealed record AnnouncementFetchResult<T>(PriceFetchStatus Status, IReadOnlyList<T> Items, string? Message = null)
{
    public bool IsOk => Status == PriceFetchStatus.Ok;
    public bool StopsRun => Status is PriceFetchStatus.QuotaExceeded or PriceFetchStatus.Unauthorized;

    public static AnnouncementFetchResult<T> Ok(IReadOnlyList<T> items) => new(PriceFetchStatus.Ok, items);
    public static AnnouncementFetchResult<T> Fail(PriceFetchStatus status, string message) => new(status, [], message);
}

/// <summary>A source of announced dividends and splits (EODHD has both feeds). Franking and DRP prices aren't in them.</summary>
public interface IAnnouncementProvider
{
    string Name { get; }

    Task<AnnouncementFetchResult<AnnouncedDividend>> GetDividendsAsync(PriceSymbol symbol, DateOnly from, CancellationToken ct = default);

    Task<AnnouncementFetchResult<AnnouncedSplit>> GetSplitsAsync(PriceSymbol symbol, DateOnly from, CancellationToken ct = default);
}
