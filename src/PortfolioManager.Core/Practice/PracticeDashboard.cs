using Dapper;
using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Clients;
using PortfolioManager.Core.Data;
using PortfolioManager.Core.Tax;

namespace PortfolioManager.Core.Practice;

/// <summary>One client's year on the practice dashboard.</summary>
public sealed record PracticeClientYear(
    ClientListItem Client,
    string Status,
    string? StatusNote,
    int ChecklistDone,
    int ChecklistItems,
    int WorkpapersPrepared,
    int WorkpapersReviewed,
    int OpenQueries,
    int AnsweredQueries,
    int Unallocated,
    DateOnly? LockedTo)
{
    /// <summary>What still needs doing, in a few words each.</summary>
    public IReadOnlyList<string> Outstanding
    {
        get
        {
            if (Status == YearStatuses.Locked)
                return [];
            var list = new List<string>();
            if (Unallocated > 0) list.Add($"{Unallocated} bank transaction{S(Unallocated)} to allocate");
            if (OpenQueries > 0) list.Add($"{OpenQueries} question{S(OpenQueries)} waiting for the client");
            if (AnsweredQueries > 0) list.Add($"{AnsweredQueries} answer{S(AnsweredQueries)} to act on");
            if (WorkpapersPrepared > WorkpapersReviewed) list.Add($"{WorkpapersPrepared - WorkpapersReviewed} workpaper{S(WorkpapersPrepared - WorkpapersReviewed)} to review");
            if (ChecklistDone < ChecklistItems) list.Add($"{ChecklistItems - ChecklistDone} of {ChecklistItems} checklist items");
            return list;
        }
    }

    private static string S(int n) => n == 1 ? "" : "s";
}

public sealed class PracticeDashboardService(IDbConnectionFactory db, ClientService clients)
{
    private sealed record Counts(int PortfolioId, string? Status, string? StatusNote, int ChecklistDone, int WorkpapersPrepared, int WorkpapersReviewed,
        int OpenQueries, int AnsweredQueries, int Unallocated, DateTime? LockedTo);

    /// <summary>Every client the login can open, for one financial year. Only <paramref name="accessibleIds"/> are read.</summary>
    public async Task<IReadOnlyList<PracticeClientYear>> GetAsync(int loginUserId, IReadOnlyCollection<int> accessibleIds, int year)
    {
        var list = await clients.ListAsync(loginUserId, accessibleIds);
        if (list.Count == 0)
            return [];
        var ids = list.Select(c => c.PortfolioId).ToArray();
        await using var con = db.Create();
        IEnumerable<Counts> counts;
        try
        {
            counts = await con.QueryAsync<Counts>("""
                SELECT P.Id AS PortfolioId,
                       S.strStatus AS Status, S.strNote AS StatusNote,
                       (SELECT COUNT(*) FROM tblYearChecklist C WHERE C.iUserID = P.Id AND C.iYear = @year AND C.bDone = 1) AS ChecklistDone,
                       (SELECT COUNT(*) FROM tblWorkpapers W WHERE W.iUserID = P.Id AND W.iYear = @year AND W.dtmPrepared IS NOT NULL) AS WorkpapersPrepared,
                       (SELECT COUNT(*) FROM tblWorkpapers W WHERE W.iUserID = P.Id AND W.iYear = @year AND W.dtmReviewed IS NOT NULL) AS WorkpapersReviewed,
                       (SELECT COUNT(*) FROM tblClientQueries Q WHERE Q.iUserID = P.Id AND Q.strStatus = 'Open') AS OpenQueries,
                       (SELECT COUNT(*) FROM tblClientQueries Q WHERE Q.iUserID = P.Id AND Q.strStatus = 'Answered') AS AnsweredQueries,
                       (SELECT COUNT(*) FROM tblJournalLines JL JOIN tblJournal J ON J.iJournalID = JL.iJournalID
                          JOIN tblChartOfAccounts CA ON CA.iAccountID = JL.iAccountID
                        WHERE J.iUserID = P.Id AND J.strSourceModule = 'BankImport' AND JL.iMatchedJournalID IS NULL
                          AND CA.strAccountName = @suspense) AS Unallocated,
                       CAST(L.dtmLockedTo AS datetime) AS LockedTo
                FROM (SELECT iUserId AS Id FROM tblUsers WHERE iUserId IN @ids) P
                LEFT JOIN tblYearStatus S ON S.iUserID = P.Id AND S.iYear = @year
                LEFT JOIN tblLockedPeriod L ON L.iUserID = P.Id
                """, new { ids, year, suspense = BankImportService.SuspenseAccountName });
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208)
        {
            counts = [];
        }
        var byId = counts.ToDictionary(c => c.PortfolioId);
        var yearEnd = FinancialYear.EndDate(year);
        return list.Select(c =>
        {
            var n = byId.GetValueOrDefault(c.PortfolioId);
            var locked = n?.LockedTo is { } l ? DateOnly.FromDateTime(l) : (DateOnly?)null;
            return new PracticeClientYear(c, YearStatuses.Effective(n?.Status, locked >= yearEnd), n?.StatusNote, n?.ChecklistDone ?? 0,
                YearEndChecklist.For(c.EntityType).Count, n?.WorkpapersPrepared ?? 0, n?.WorkpapersReviewed ?? 0, n?.OpenQueries ?? 0,
                n?.AnsweredQueries ?? 0, n?.Unallocated ?? 0, locked);
        }).ToList();
    }
}
