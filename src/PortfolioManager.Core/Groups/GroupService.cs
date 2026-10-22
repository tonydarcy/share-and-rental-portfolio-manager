using System.Data;
using Dapper;
using PortfolioManager.Core.Data;

namespace PortfolioManager.Core.Groups;

/// <summary>A portfolio's own grouping of trades (tblCustomGroups), such as "Long term" or "Kids' shares".</summary>
public sealed record GroupSummary(int GroupId, string Name, int Trades, int Stocks);

/// <summary>Trades to add to and take out of a group to match what was ticked.</summary>
public sealed record GroupChanges(IReadOnlyList<int> Add, IReadOnlyList<int> Remove)
{
    public bool IsEmpty => Add.Count == 0 && Remove.Count == 0;
}

/// <summary>
/// Creating, renaming and deleting custom groups and choosing their trades. The Holdings page
/// already filters by group (GetProfitSummaryv2's iGroupID).
/// </summary>
public sealed class GroupService(IDbConnectionFactory db)
{
    public const int MaxNameLength = 100;

    public async Task<IReadOnlyList<GroupSummary>> GetGroupsAsync(int portfolioId)
    {
        await using var con = db.Create();
        var groups = await con.QueryAsync<GroupSummary>("""
            SELECT G.iGroupID AS GroupId, G.strGroupName AS Name,
                   COUNT(T.iShareTradeId) AS Trades,
                   COUNT(DISTINCT T.strStockExchangeCode + ':' + T.strStockCode) AS Stocks
            FROM tblCustomGroups G
            LEFT JOIN tblShareTradeGroups L ON L.iGroupID = G.iGroupID
            LEFT JOIN tblShareTrades T ON T.iShareTradeId = L.iShareTradeID AND T.iUserId = G.iUserID
            WHERE G.iUserID = @portfolioId
            GROUP BY G.iGroupID, G.strGroupName
            ORDER BY G.strGroupName
            """, new { portfolioId });
        return groups.ToList();
    }

    /// <summary>The ids of the portfolio's trades in a group.</summary>
    public async Task<IReadOnlySet<int>> GetTradeIdsAsync(int portfolioId, int groupId)
    {
        await using var con = db.Create();
        var ids = await con.QueryAsync<int>("""
            SELECT L.iShareTradeID FROM tblShareTradeGroups L
            JOIN tblCustomGroups G ON G.iGroupID = L.iGroupID
            JOIN tblShareTrades T ON T.iShareTradeId = L.iShareTradeID
            WHERE G.iGroupID = @groupId AND G.iUserID = @portfolioId AND T.iUserId = @portfolioId
            """, new { portfolioId, groupId });
        return ids.ToHashSet();
    }

    public async Task<ProcResult> CreateAsync(int portfolioId, string name)
    {
        await using var con = db.Create();
        var row = (IDictionary<string, object?>)await con.QuerySingleAsync("uspCreateCustomGroup",
            new { iUserId = portfolioId, strGroupName = name.Trim() }, commandType: CommandType.StoredProcedure);
        var id = row.TryGetValue("iGroupID", out var v) && v is not null and not DBNull ? Convert.ToInt32(v) : (int?)null;
        return ProcResult.From(row.TryGetValue("Result", out var r) ? Convert.ToString(r) : null, "Group Created", id);
    }

    public async Task<ProcResult> RenameAsync(int portfolioId, int groupId, string name)
    {
        await using var con = db.Create();
        var result = await con.ExecuteScalarAsync<string>("uspRenameCustomGroup",
            new { iUserId = portfolioId, iGroupID = groupId, strGroupName = name.Trim() }, commandType: CommandType.StoredProcedure);
        return ProcResult.From(result, "Group Renamed");
    }

    public async Task<ProcResult> DeleteAsync(int portfolioId, int groupId)
    {
        await using var con = db.Create();
        var result = await con.ExecuteScalarAsync<string>("uspDeleteCustomGroup",
            new { iUserId = portfolioId, iGroupID = groupId }, commandType: CommandType.StoredProcedure);
        return ProcResult.From(result, "Group Deleted");
    }

    /// <summary>Adds and removes trades with uspAddTradeToGroup / uspRemoveTradeFromGroup, all or nothing.</summary>
    public async Task ApplyAsync(int portfolioId, int groupId, GroupChanges changes)
    {
        if (changes.IsEmpty)
            return;
        await using var con = db.Create();
        await con.OpenAsync();
        await using var tx = await con.BeginTransactionAsync();
        foreach (var tradeId in changes.Add)
            await con.ExecuteAsync("uspAddTradeToGroup",
                new { iUserId = portfolioId, iShareTradeID = tradeId, iGroupID = groupId }, tx, commandType: CommandType.StoredProcedure);
        foreach (var tradeId in changes.Remove)
            await con.ExecuteAsync("uspRemoveTradeFromGroup",
                new { iUserId = portfolioId, iShareTradeID = tradeId, iGroupID = groupId }, tx, commandType: CommandType.StoredProcedure);
        await tx.CommitAsync();
    }

    /// <summary>
    /// What to change so the group holds exactly the ticked trades. Ticked ids that aren't the
    /// portfolio's trades are ignored, so a tampered form can't reach another portfolio's trades.
    /// </summary>
    public static GroupChanges Changes(IEnumerable<int> ticked, IReadOnlySet<int> inGroup, IReadOnlySet<int> portfolioTrades)
    {
        var wanted = ticked.Where(portfolioTrades.Contains).ToHashSet();
        return new GroupChanges(
            wanted.Where(id => !inGroup.Contains(id)).Order().ToList(),
            inGroup.Where(id => !wanted.Contains(id)).Order().ToList());
    }

    public static string? ValidateName(string? name)
    {
        var n = name?.Trim();
        if (string.IsNullOrEmpty(n))
            return "Enter a name for the group.";
        if (n.Length > MaxNameLength)
            return $"The group name can be at most {MaxNameLength} characters.";
        return null;
    }
}
