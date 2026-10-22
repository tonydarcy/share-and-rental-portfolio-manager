using Dapper;
using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Data;

namespace PortfolioManager.Core.Practice;

/// <summary>One row added, changed or deleted (tblAuditLog, written by triggers).</summary>
public sealed record AuditEntry(long Id, DateTime Changed, string? ChangedBy, string? App, string Table, int RecordId, string Action,
    DateOnly? RecordDate, string? Summary)
{
    /// <summary>Set when the record's date was in a locked period at some time before this change (the year was unlocked to change it).</summary>
    public bool AfterLock { get; init; }

    public string Who => ChangedBy ?? (string.IsNullOrEmpty(App) ? "Unknown" : $"No login ({App})");

    public static string TableLabel(string t) => t switch
    {
        "CorporateAction" => "Corporate action",
        "Cash" => "Cash transaction",
        _ => t,
    };
}

public static class AuditRules
{
    public static readonly IReadOnlyList<string> Tables = ["Journal", "Trade", "Payout", "CorporateAction", "Cash", "Invoice"];

    /// <summary>
    /// Marks the changes to records dated in a period that had been locked before the change was made: the
    /// changes made after a year was signed off and then unlocked.
    /// </summary>
    public static IReadOnlyList<AuditEntry> MarkAfterLock(IEnumerable<AuditEntry> entries, IReadOnlyList<LockChange> history) =>
        entries.Select(e => e with
        {
            AfterLock = e.RecordDate is { } d && history.Any(h => h.Changed <= e.Changed && h.LockedTo is { } to && d <= to),
        }).ToList();
}

/// <summary>Reads the audit trail. The triggers in 2026-10-21-15-attachments-audit.sql write it.</summary>
public sealed class AuditTrailService(IDbConnectionFactory db, LockedPeriodService locks)
{
    private sealed record Row(long Id, DateTime Changed, string? ChangedBy, string? App, string Table, int RecordId, string Action, DateTime? RecordDate, string? Summary);

    /// <summary>The newest changes first, at most <paramref name="max"/>. Empty until the migration has been run.</summary>
    public async Task<IReadOnlyList<AuditEntry>> ListAsync(int portfolioId, DateOnly from, DateOnly to, string? table, bool afterLockOnly, int max = 1000)
    {
        await using var con = db.Create();
        IEnumerable<Row> rows;
        try
        {
            rows = await con.QueryAsync<Row>("""
                SELECT TOP (@max) A.iAuditID AS Id, A.dtmChanged AS Changed, U.strLoginEmail AS ChangedBy, A.strApp AS App, A.strTable AS [Table],
                       A.iRecordID AS RecordId, A.strAction AS Action, CAST(A.dtmRecordDate AS datetime) AS RecordDate, A.strSummary AS Summary
                FROM tblAuditLog A LEFT JOIN tblUsers U ON U.iUserId = A.iChangedByUserID
                WHERE A.iUserID = @portfolioId AND A.dtmChanged >= @from AND A.dtmChanged < DATEADD(DAY, 1, @to)
                  AND (@table IS NULL OR A.strTable = @table)
                ORDER BY A.iAuditID DESC
                """, new { portfolioId, from = from.ToDateTime(TimeOnly.MinValue), to = to.ToDateTime(TimeOnly.MinValue), table, max = afterLockOnly ? 20000 : max });
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208)
        {
            return [];
        }
        var history = await locks.GetHistoryAsync(portfolioId);
        var entries = AuditRules.MarkAfterLock(rows.Select(r => new AuditEntry(r.Id, r.Changed, r.ChangedBy, r.App, r.Table, r.RecordId, r.Action,
            r.RecordDate is { } d ? DateOnly.FromDateTime(d) : null, r.Summary)), history);
        return afterLockOnly ? entries.Where(e => e.AfterLock).Take(max).ToList() : entries;
    }
}
