namespace PortfolioManager.Core.Accounting;

/// <summary>One account from the chart of accounts (uspGetChartOfAccounts).</summary>
public sealed record LedgerAccount(
    int AccountId,
    int Number,
    string Name,
    string Type,
    string? Currency,
    int OwnerId,
    string ParentName,
    int? ParentId,
    bool? Postable)
{
    /// <summary>System accounts (iUserID 0) are shared by every portfolio and are read-only.</summary>
    public bool IsSystem => OwnerId == 0;

    /// <summary>Only an account explicitly marked not postable counts as inactive (older databases don't say).</summary>
    public bool IsInactive => Postable == false;

    public string Display => $"{Number} - {Name}";
}

/// <summary>An account picked from a dropdown (uspGetAccountsForJournal / uspGetAccountsForImport).</summary>
public sealed record AccountChoice(int AccountId, string DisplayName);

public static class AccountTypes
{
    public static readonly IReadOnlyList<string> All = ["Asset", "Liability", "Equity", "Revenue", "Expense"];

    public static string NumberHint(string type) => type switch
    {
        "Asset" => "1xxx",
        "Liability" => "2xxx",
        "Equity" => "3xxx",
        "Revenue" => "4xxx",
        "Expense" => "5xxx or 6xxx",
        _ => "",
    };

    /// <summary>
    /// A free account number when none is given: the next after the parent's for a sub-account (2301, 2302
    /// under 2300 Investment Loans), otherwise the next free ten in the type's range (6010 for an expense).
    /// Null when that range is full.
    /// </summary>
    public static int? NextNumber(IReadOnlyList<LedgerAccount> accounts, LedgerAccount? parent, string type)
    {
        var used = accounts.Select(a => a.Number).ToHashSet();
        if (parent is not null)
        {
            for (var n = parent.Number + 1; n <= Math.Min(parent.Number + 99, 999999); n++)
                if (!used.Contains(n))
                    return n;
            return null;
        }
        var start = type switch { "Asset" => 1000, "Liability" => 2000, "Equity" => 3000, "Revenue" => 4000, "Expense" => 6000, _ => 0 };
        if (start == 0)
            return null;
        for (var n = start + 10; n < start + 1000; n += 10)
            if (!used.Contains(n))
                return n;
        return null;
    }
}

/// <summary>The fields of the add / edit account form.</summary>
public sealed class AccountInput
{
    public int? Number { get; set; }
    public string Name { get; set; } = "";
    public string Type { get; set; } = "Expense";
    public int? ParentId { get; set; }

    /// <summary>
    /// Checks the form before the proc does. <paramref name="accounts"/> is the portfolio's chart of
    /// accounts; the parent must be one of them, and not the account being edited.
    /// </summary>
    public IReadOnlyList<string> Validate(IReadOnlyList<LedgerAccount> accounts, int? editingId = null)
    {
        var errors = new List<string>();
        if (Number is null or <= 0 or > 999999)
            errors.Add("Enter an account number from 1 to 999999.");
        else if (accounts.Any(a => a.Number == Number && a.AccountId != editingId))
            errors.Add($"Account number {Number} is already used.");

        var name = Name?.Trim() ?? "";
        if (name.Length == 0)
            errors.Add("Enter the account name.");
        else if (name.Length > 255)
            errors.Add("The account name can be at most 255 characters.");
        else if (accounts.Any(a => string.Equals(a.Name.Trim(), name, StringComparison.OrdinalIgnoreCase) && a.AccountId != editingId))
            errors.Add($"There is already an account called \"{name}\".");

        if (!AccountTypes.All.Contains(Type))
            errors.Add("Choose the account type.");

        if (ParentId is { } parentId)
        {
            if (parentId == editingId)
                errors.Add("An account can't sit under itself.");
            else if (accounts.All(a => a.AccountId != parentId))
                errors.Add("Choose a parent account from the list.");
        }
        return errors;
    }

    public static AccountInput From(LedgerAccount a) => new()
    {
        Number = a.Number,
        Name = a.Name,
        Type = a.Type,
        ParentId = a.ParentId,
    };
}

/// <summary>strAccountingMode on tblUsers: SingleEntry, Hybrid or DoubleEntry.</summary>
public static class AccountingMode
{
    /// <summary>Trades, cash and payouts post journals only in Hybrid and DoubleEntry mode (as the procs check).</summary>
    public static bool PostsJournals(string? mode) =>
        string.Equals(mode?.Trim(), "Hybrid", StringComparison.OrdinalIgnoreCase)
        || string.Equals(mode?.Trim(), "DoubleEntry", StringComparison.OrdinalIgnoreCase);
}
