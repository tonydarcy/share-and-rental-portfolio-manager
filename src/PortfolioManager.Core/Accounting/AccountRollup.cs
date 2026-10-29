namespace PortfolioManager.Core.Accounting;

/// <summary>
/// Groups sub-accounts under their parent on the balance sheet and profit and loss, e.g. each loan under
/// Property Loans. The procs list every account on its own line, by name; this puts a parent's line first
/// with the total of it and its sub-accounts (RowType Group), and the accounts indented under it.
/// </summary>
public static class AccountRollup
{
    public const string GroupRow = "Group";

    public static IReadOnlyList<StatementLine> Apply(IReadOnlyList<StatementLine> lines, IReadOnlyList<LedgerAccount> accounts)
    {
        var byName = new Dictionary<string, LedgerAccount>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in accounts)
            byName.TryAdd(a.Name.Trim(), a);
        var byId = accounts.GroupBy(a => a.AccountId).ToDictionary(g => g.Key, g => g.First());

        var result = new List<StatementLine>();
        for (var i = 0; i < lines.Count;)
        {
            if (lines[i].RowType != "Item")
            {
                result.Add(lines[i++]);
                continue;
            }
            // A run of items is one section's accounts
            var run = new List<StatementLine>();
            while (i < lines.Count && lines[i].RowType == "Item")
                run.Add(lines[i++]);
            result.AddRange(Group(run, byName, byId));
        }
        return result;
    }

    private static IEnumerable<StatementLine> Group(List<StatementLine> run, Dictionary<string, LedgerAccount> byName, Dictionary<int, LedgerAccount> byId)
    {
        // Each item's top-level parent of the same type; lines that aren't accounts (retained earnings) stand alone.
        // Groups keep the place of their first line, so the procs' order is kept.
        var groups = new List<(LedgerAccount? Top, string Key, List<(StatementLine Line, LedgerAccount? Account)> Members)>();
        foreach (var line in run)
        {
            var account = byName.GetValueOrDefault(line.Label.Trim());
            var top = account is null ? null : Top(account, byId);
            var key = top is null ? "\0" + line.Label : top.AccountId.ToString();
            var group = groups.FirstOrDefault(g => g.Key == key);
            if (group.Members is null)
                groups.Add(group = (top, key, []));
            group.Members.Add((line, account));
        }

        foreach (var (top, _, members) in groups)
        {
            if (top is null || (members.Count == 1 && members[0].Account?.AccountId == top.AccountId))
            {
                foreach (var m in members)
                    yield return m.Line;
                continue;
            }
            var first = members[0].Line;
            yield return first with { Label = top.Name, Amount = members.Sum(m => m.Line.Amount ?? 0), RowType = GroupRow };
            // The parent's own postings first, then the sub-accounts by number
            foreach (var m in members.OrderBy(m => m.Account!.AccountId == top.AccountId ? 0 : 1).ThenBy(m => m.Account!.Number))
                yield return m.Line with
                {
                    Label = m.Account!.AccountId == top.AccountId ? $"{top.Name} (not in a sub-account)" : m.Line.Label,
                    Indent = first.Indent + 1,
                };
        }
    }

    /// <summary>The highest parent of the same type (a loan under a liability header), guarding against loops.</summary>
    public static LedgerAccount Top(LedgerAccount account, IReadOnlyDictionary<int, LedgerAccount> byId)
    {
        var top = account;
        var seen = new HashSet<int> { account.AccountId };
        while (top.ParentId is { } parentId && byId.TryGetValue(parentId, out var parent)
               && string.Equals(parent.Type, account.Type, StringComparison.OrdinalIgnoreCase) && seen.Add(parent.AccountId))
            top = parent;
        return top;
    }
}
