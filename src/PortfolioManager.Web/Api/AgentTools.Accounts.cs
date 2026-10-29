using PortfolioManager.Core.Accounting;

namespace PortfolioManager.Web.Api;

/// <summary>
/// The chart of accounts tools, as the Accounts page does them. A sub-account (one with a parent) shows under its
/// parent on the balance sheet and profit and loss, with the parent's line the total of them all.
/// </summary>
public sealed partial class AgentTools
{
    private static List<AgentTool> BuildAccounts() =>
    [
        new("list_chart_of_accounts", "List the chart of accounts",
            "Every account in the portfolio's chart of accounts: id, number, name, type, parent and whether it's a shared system "
            + "account (read only) or inactive (hidden from allocations).",
            false, new ToolSchema().Portfolio().Build(),
            ListChart),

        new("create_ledger_account", "Create a ledger account",
            "Adds an account to the chart of accounts, as the Accounts page does. Give parentAccountId to make it a sub-account, "
            + "e.g. one account per loan under Property Loans, so each loan has its own balance and the parent shows their total. "
            + "type defaults to the parent's; number defaults to the next free one after the parent's (or in the type's range).",
            true, new ToolSchema().Portfolio()
                .String("name", "The account name, e.g. Loan - CBA 123456 (unique in the chart)", required: true)
                .String("type", "Account type (required without a parent)", choices: [.. AccountTypes.All])
                .Int("number", "Account number, 1 to 999999 and unused (default: the next free one)")
                .Int("parentAccountId", "The parent account, from list_chart_of_accounts").Build(),
            CreateAccount),

        new("edit_ledger_account", "Change a ledger account",
            "Renames or renumbers one of the portfolio's own accounts, or moves it under a parent (parentAccountId 0 makes it top "
            + "level). Send only what changes. System accounts can't be changed. Its postings stay on it.",
            true, new ToolSchema().Portfolio()
                .Int("accountId", "The account, from list_chart_of_accounts", required: true)
                .String("name", "New name")
                .String("type", "New account type", choices: [.. AccountTypes.All])
                .Int("number", "New account number")
                .Int("parentAccountId", "New parent account, or 0 for top level").Build(),
            EditAccount),
    ];

    private static async Task<object?> ListChart(AgentCall c) =>
        (await c.Service<ChartOfAccountsService>().GetAccountsAsync(c.PortfolioId)).Select(a => new
        {
            a.AccountId,
            a.Number,
            a.Name,
            a.Type,
            a.Currency,
            ParentAccountId = a.ParentId,
            ParentName = a.ParentName.Length > 0 ? a.ParentName : null,
            System = a.IsSystem,
            Inactive = a.IsInactive,
        });

    private static async Task<object?> CreateAccount(AgentCall c)
    {
        var chart = c.Service<ChartOfAccountsService>();
        var accounts = await chart.GetAccountsAsync(c.PortfolioId);
        var input = NewAccount(accounts, c.String("name"), c.String("type"), c.Int("number"), c.Int("parentAccountId"));
        var result = await chart.AddAsync(c.PortfolioId, input);
        if (!result.Ok)
            throw new AgentToolException(400, result.Error!);
        var added = (await chart.GetAccountsAsync(c.PortfolioId)).FirstOrDefault(a => a.Number == input.Number);
        return new { Message = $"Added account {input.Number} {input.Name.Trim()}.", AccountId = added?.AccountId, input.Number };
    }

    /// <summary>The form for a new account, filling the type from the parent and a free number when they're left out.</summary>
    public static AccountInput NewAccount(IReadOnlyList<LedgerAccount> accounts, string? name, string? type, int? number, int? parentId)
    {
        var parent = parentId is { } id
            ? accounts.FirstOrDefault(a => a.AccountId == id)
                ?? throw new AgentToolException(404, "There is no account with that parentAccountId. list_chart_of_accounts gives the ids.")
            : null;
        type = Type(type) ?? parent?.Type ?? throw new AgentToolException(400, "type is required for an account with no parent.");
        number ??= AccountTypes.NextNumber(accounts, parent, type)
            ?? throw new AgentToolException(400, "There's no free number near there; give a number.");
        return new AccountInput { Name = name ?? "", Type = type, Number = number, ParentId = parent?.AccountId };
    }

    private static async Task<object?> EditAccount(AgentCall c)
    {
        var chart = c.Service<ChartOfAccountsService>();
        var accountId = c.RequiredInt("accountId");
        var accounts = await chart.GetAccountsAsync(c.PortfolioId);
        if (ChartOfAccountsService.OwnAccount(accounts, c.PortfolioId, accountId) is { } refusal)
            throw new AgentToolException(refusal.Error!.StartsWith("System") ? 409 : 404, refusal.Error);

        var input = AccountInput.From(accounts.First(a => a.AccountId == accountId));
        if (c.String("name") is { } name)
            input.Name = name;
        if (Type(c.String("type")) is { } type)
            input.Type = type;
        if (c.Int("number") is { } number)
            input.Number = number;
        if (c.Int("parentAccountId") is { } parentId)
            input.ParentId = parentId == 0 ? null : parentId;

        var result = await chart.UpdateAsync(c.PortfolioId, accountId, input);
        if (!result.Ok)
            throw new AgentToolException(400, result.Error!);
        return new { Message = $"Saved account {input.Number} {input.Name.Trim()}.", AccountId = accountId };
    }

    /// <summary>An account type in any case, as the form spells it.</summary>
    private static string? Type(string? type) => type is null
        ? null
        : AccountTypes.All.FirstOrDefault(t => string.Equals(t, type, StringComparison.OrdinalIgnoreCase))
            ?? throw new AgentToolException(400, $"type must be one of {string.Join(", ", AccountTypes.All)}.");
}
