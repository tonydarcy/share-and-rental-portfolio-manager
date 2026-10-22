using System.Data;
using Dapper;
using PortfolioManager.Core.Data;

namespace PortfolioManager.Core.Accounting;

public static class RuleDirection
{
    public const string Any = "Any";
    public const string Deposit = "Deposit";
    public const string Withdrawal = "Withdrawal";

    public static readonly IReadOnlyList<string> All = [Any, Deposit, Withdrawal];

    public static string Label(string direction) => direction switch
    {
        Deposit => "Deposits",
        Withdrawal => "Withdrawals",
        _ => "Deposits and withdrawals",
    };
}

/// <summary>"Bank lines containing Contains go to AccountId" (with a tax code, if TaxCodeId is set), tried in SortOrder.</summary>
public sealed record AllocationRule(int RuleId, string Contains, string Direction, int AccountId, string AccountName, string? Description, int SortOrder,
    int? TaxCodeId = null, string? TaxCode = null)
{
    /// <summary>Case and spacing don't matter: "ATO  payment" finds "ato payment".</summary>
    public bool Matches(UnallocatedLine line)
    {
        if (Direction == RuleDirection.Deposit && !line.IsDeposit) return false;
        if (Direction == RuleDirection.Withdrawal && line.IsDeposit) return false;
        var text = Normalise(Contains);
        return text.Length > 0 && Normalise(line.Description).Contains(text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The first rule that matches the line, in the order they are tried.</summary>
    public static AllocationRule? FirstMatch(IEnumerable<AllocationRule> rules, UnallocatedLine line) =>
        rules.OrderBy(r => r.SortOrder).ThenBy(r => r.RuleId).FirstOrDefault(r => r.Matches(line));

    public static string Normalise(string? text) =>
        string.Join(' ', (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// A starting point for a rule made from a bank line: its description without the digits that
    /// change each time (dates, receipt and card numbers), trimmed to the words before them.
    /// </summary>
    public static string Suggest(string description)
    {
        var words = Normalise(description).Split(' ');
        var kept = words.TakeWhile(w => !w.Any(char.IsDigit)).ToList();
        var text = string.Join(' ', kept.Count > 0 ? kept : words.Take(3));
        return text.Length > 200 ? text[..200] : text;
    }
}

public sealed class AllocationRuleInput
{
    public int? RuleId { get; set; }
    public string Contains { get; set; } = "";
    public string Direction { get; set; } = RuleDirection.Any;
    public int AccountId { get; set; }
    public string? Description { get; set; }
    public int? SortOrder { get; set; }
    public int? TaxCodeId { get; set; }
}

public sealed record RulesApplied(int Allocated, int Skipped, IReadOnlyList<string> Errors);

/// <summary>Allocation rules for imported bank lines, and applying them to what's still unallocated.</summary>
public sealed class AllocationRuleService(IDbConnectionFactory db, BankImportService imports)
{
    public async Task<IReadOnlyList<AllocationRule>> GetAsync(int portfolioId)
    {
        await using var con = db.Create();
        var rows = await con.QueryAsync("uspGetAllocationRules", new { iUserId = portfolioId }, commandType: CommandType.StoredProcedure);
        return Map(rows.Cast<IDictionary<string, object?>>());
    }

    public static IReadOnlyList<AllocationRule> Map(IEnumerable<IDictionary<string, object?>> rows) =>
        rows.Select(r => new AllocationRule(
                Db.Int(r, "iRuleID") ?? 0,
                Db.Str(r, "strContains"),
                Db.Str(r, "strDirection") is { Length: > 0 } d ? d : RuleDirection.Any,
                Db.Int(r, "iAccountID") ?? 0,
                Db.Int(r, "iAccountNumber") is { } n ? $"{n} - {Db.Str(r, "strAccountName")}" : Db.Str(r, "strAccountName"),
                Db.Str(r, "strDescription") is { Length: > 0 } t ? t : null,
                Db.Int(r, "iSortOrder") ?? 0,
                Db.Int(r, "iTaxCodeID"),
                Db.Str(r, "strTaxCode") is { Length: > 0 } c ? c : null))
            .Where(r => r.RuleId > 0)
            .ToList();

    public async Task<ProcResult> SaveAsync(int portfolioId, AllocationRuleInput input)
    {
        await using var con = db.Create();
        var row = await con.QuerySingleOrDefaultAsync<(string? Result, int? RuleId)>("uspSaveAllocationRule",
            SaveParameters(portfolioId, input), commandType: CommandType.StoredProcedure);
        return ProcResult.From(row.Result, "Success", row.RuleId);
    }

    /// <summary>The tax code is only sent when there is one (the proc's default clears it), so saving works before the sales tax migration.</summary>
    public static DynamicParameters SaveParameters(int portfolioId, AllocationRuleInput input)
    {
        var p = new DynamicParameters(new
        {
            iUserId = portfolioId,
            iRuleID = input.RuleId,
            strContains = input.Contains?.Trim() ?? "",
            strDirection = RuleDirection.All.Contains(input.Direction) ? input.Direction : RuleDirection.Any,
            iAccountID = input.AccountId,
            strDescription = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim(),
            iSortOrder = input.SortOrder,
        });
        if (input.TaxCodeId is > 0)
            p.Add("iTaxCodeID", input.TaxCodeId);
        return p;
    }

    public async Task<ProcResult> DeleteAsync(int portfolioId, int ruleId)
    {
        await using var con = db.Create();
        var result = await con.QuerySingleOrDefaultAsync<string?>("uspDeleteAllocationRule",
            new { iUserId = portfolioId, iRuleID = ruleId }, commandType: CommandType.StoredProcedure);
        return ProcResult.From(result, "Success");
    }

    /// <summary>Each unallocated line and the rule that would allocate it, if any.</summary>
    public async Task<IReadOnlyDictionary<int, AllocationRule>> SuggestAsync(int portfolioId, IEnumerable<UnallocatedLine> lines)
    {
        var rules = await GetAsync(portfolioId);
        return lines.Select(l => (l.LineId, Rule: AllocationRule.FirstMatch(rules, l)))
            .Where(x => x.Rule is not null)
            .ToDictionary(x => x.LineId, x => x.Rule!);
    }

    /// <summary>
    /// Allocates every unallocated line a rule matches, each one as the Assign button would.
    /// A line in a locked year, or one another session allocated first, is skipped.
    /// </summary>
    public async Task<RulesApplied> ApplyAsync(int portfolioId)
    {
        var rules = await GetAsync(portfolioId);
        if (rules.Count == 0)
            return new RulesApplied(0, 0, []);

        int allocated = 0, skipped = 0;
        var errors = new List<string>();
        foreach (var line in await imports.GetUnallocatedAsync(portfolioId))
        {
            if (AllocationRule.FirstMatch(rules, line) is not { } rule)
                continue;
            AllocationResult result;
            try
            {
                result = await imports.AllocateToAccountAsync(portfolioId, line.LineId, rule.AccountId, rule.Description, rule.TaxCodeId);
            }
            catch (Exception ex) when (LockedPeriodService.IsLockError(ex))
            {
                skipped++;
                continue;
            }
            if (result.Ok)
                allocated++;
            else
            {
                skipped++;
                if (errors.Count < 5)
                    errors.Add($"{line.Date:d MMM yyyy} {line.Description}: {result.Error}");
            }
        }
        return new RulesApplied(allocated, skipped, errors);
    }
}
