using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using PortfolioManager.Core.Data;
using PortfolioManager.Core.Regions;
using PortfolioManager.Core.Tax;

namespace PortfolioManager.Core.Accounting;

/// <summary>A portfolio's lock: nothing dated on or before LockedTo can be added, changed or deleted.</summary>
public sealed record LockedPeriod(DateOnly? LockedTo, DateTime? Changed = null, string? ChangedBy = null)
{
    public static readonly LockedPeriod None = new((DateOnly?)null);

    public bool IsLocked(DateOnly date) => LockedTo is { } to && date <= to;

    /// <summary>Whether the whole financial year is locked.</summary>
    public bool IsYearLocked(int endYear, YearStart? start = null) => IsLocked(FinancialYear.EndDate(endYear, start));

    /// <summary>
    /// Where the lock goes when a year is unlocked: the end of the year before, so earlier years
    /// stay locked.
    /// </summary>
    public static DateOnly UnlockTo(int endYear, YearStart? start = null) => FinancialYear.EndDate(endYear - 1, start);
}

public sealed record LockChange(DateOnly? LockedTo, DateTime Changed, string ChangedBy);

/// <summary>
/// Locking a financial year once the statements are signed. The database enforces it with triggers
/// (error <see cref="ErrorNumber"/>), so every route in, old site included, is refused.
/// </summary>
public sealed class LockedPeriodService(IDbConnectionFactory db)
{
    public const int ErrorNumber = 50300;

    /// <summary>The database refused a change because it is in a locked period.</summary>
    public static bool IsLockError(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException)
            if (ex is SqlException { Number: ErrorNumber })
                return true;
        return false;
    }

    public async Task<LockedPeriod> GetAsync(int portfolioId)
    {
        await using var con = db.Create();
        var row = (await con.QueryAsync("uspGetLockedPeriod", new { iUserId = portfolioId }, commandType: CommandType.StoredProcedure))
            .Cast<IDictionary<string, object?>>().FirstOrDefault();
        return Map(row);
    }

    public static LockedPeriod Map(IDictionary<string, object?>? row) =>
        row is not null && Db.Date(row, "dtmLockedTo") is { } to
            ? new LockedPeriod(to, Db.Timestamp(row, "dtmChangedDate"), Db.Str(row, "strChangedBy") is { Length: > 0 } by ? by : null)
            : LockedPeriod.None;

    public async Task<IReadOnlyList<LockChange>> GetHistoryAsync(int portfolioId)
    {
        await using var con = db.Create();
        var rows = await con.QueryAsync("uspGetLockedPeriodHistory", new { iUserId = portfolioId }, commandType: CommandType.StoredProcedure);
        return rows.Cast<IDictionary<string, object?>>()
            .Select(r => new LockChange(Db.Date(r, "dtmLockedTo"), Db.Timestamp(r, "dtmChangedDate") ?? default, Db.Str(r, "strChangedBy")))
            .ToList();
    }

    /// <summary>Locks up to the date, moves the lock, or (null) removes it.</summary>
    public async Task<ProcResult> SetAsync(int portfolioId, DateOnly? lockedTo, int changedByUserId)
    {
        await using var con = db.Create();
        var result = await con.QuerySingleOrDefaultAsync<string?>("uspSetLockedPeriod", new
        {
            iUserId = portfolioId,
            dtLockedTo = lockedTo?.ToDateTime(TimeOnly.MinValue),
            iChangedByUserID = changedByUserId,
        }, commandType: CommandType.StoredProcedure);
        return ProcResult.From(result, "Success");
    }
}
