using System.Data;
using Dapper;
using PortfolioManager.Core.Data;

namespace PortfolioManager.Core.Accounting;

/// <summary>The outcome of a chart of accounts change: null when it worked, otherwise why not.</summary>
public sealed record AccountResult(string? Error)
{
    public bool Ok => Error is null;
    public static readonly AccountResult Success = new((string?)null);
}

/// <summary>The chart of accounts (ChartOfAccounts.aspx), through the ledger procs.</summary>
public sealed class ChartOfAccountsService(IDbConnectionFactory db)
{
    /// <summary>The portfolio's own accounts and the system ones, by number.</summary>
    public async Task<IReadOnlyList<LedgerAccount>> GetAccountsAsync(int portfolioId)
    {
        await using var con = db.Create();
        var rows = await con.QueryAsync("uspGetChartOfAccounts", new { iUserId = portfolioId },
            commandType: CommandType.StoredProcedure);
        return Map(rows.Cast<IDictionary<string, object?>>());
    }

    /// <summary>
    /// iParentAccountID and bIsPostable only come back once 2026-10-15-accounting.sql has run;
    /// before that they read as unknown.
    /// </summary>
    public static IReadOnlyList<LedgerAccount> Map(IEnumerable<IDictionary<string, object?>> rows) =>
        rows.Select(r => new LedgerAccount(
                Db.Int(r, "iAccountID") ?? 0,
                Db.Int(r, "iAccountNumber") ?? 0,
                Db.Str(r, "strAccountName"),
                Db.Str(r, "strAccountType"),
                Db.Str(r, "strCurrency") is { Length: > 0 } c ? c : null,
                Db.Int(r, "iUserID") ?? 0,
                Db.Str(r, "ParentAccountName") is { Length: > 0 } p and not "-" ? p : "",
                Db.Int(r, "iParentAccountID"),
                Db.Bool(r, "bIsPostable")))
            .Where(a => a.AccountId > 0)
            .ToList();

    /// <summary>Postable accounts for a journal line or an allocation (uspGetAccountsForJournal).</summary>
    public async Task<IReadOnlyList<AccountChoice>> GetJournalAccountsAsync(int portfolioId)
    {
        await using var con = db.Create();
        var rows = await con.QueryAsync("uspGetAccountsForJournal", new { iUserId = portfolioId },
            commandType: CommandType.StoredProcedure);
        return MapChoices(rows.Cast<IDictionary<string, object?>>());
    }

    /// <summary>Balance sheet accounts a statement can be imported into, bank accounts first (uspGetAccountsForImport).</summary>
    public async Task<IReadOnlyList<AccountChoice>> GetImportAccountsAsync(int portfolioId)
    {
        await using var con = db.Create();
        var rows = await con.QueryAsync("uspGetAccountsForImport", new { iUserId = portfolioId },
            commandType: CommandType.StoredProcedure);
        return MapChoices(rows.Cast<IDictionary<string, object?>>());
    }

    public static IReadOnlyList<AccountChoice> MapChoices(IEnumerable<IDictionary<string, object?>> rows) =>
        rows.Select(r => new AccountChoice(Db.Int(r, "iAccountID") ?? 0, Db.Str(r, "DisplayName")))
            .Where(a => a.AccountId > 0)
            .ToList();

    /// <summary>The portfolio's strAccountingMode (SingleEntry, Hybrid or DoubleEntry).</summary>
    public async Task<string> GetAccountingModeAsync(int portfolioId)
    {
        await using var con = db.Create();
        var mode = await con.QuerySingleOrDefaultAsync<string?>(
            "SELECT strAccountingMode FROM tblUsers WHERE iUserId = @portfolioId", new { portfolioId });
        return mode?.Trim() ?? "SingleEntry";
    }

    /// <summary>Adds one of the portfolio's own accounts with uspAddChartOfAccount.</summary>
    public async Task<AccountResult> AddAsync(int portfolioId, AccountInput input)
    {
        var accounts = await GetAccountsAsync(portfolioId);
        var errors = input.Validate(accounts);
        if (errors.Count > 0)
            return new AccountResult(errors[0]);

        await using var con = db.Create();
        var result = await con.QuerySingleOrDefaultAsync<string?>("uspAddChartOfAccount", new
        {
            iUserId = portfolioId,
            iAccountNumber = input.Number!.Value,
            strAccountName = input.Name.Trim(),
            strAccountType = input.Type,
            iParentAccountID = input.ParentId,
        }, commandType: CommandType.StoredProcedure);
        return Result(result);
    }

    /// <summary>
    /// Edits one of the portfolio's own accounts with uspUpdateChartOfAccount. System accounts and
    /// accounts of other portfolios are refused before the proc is called.
    /// </summary>
    public async Task<AccountResult> UpdateAsync(int portfolioId, int accountId, AccountInput input)
    {
        var accounts = await GetAccountsAsync(portfolioId);
        if (OwnAccount(accounts, portfolioId, accountId) is { } refusal)
            return refusal;
        var errors = input.Validate(accounts, accountId);
        if (errors.Count > 0)
            return new AccountResult(errors[0]);

        await using var con = db.Create();
        var result = await con.QuerySingleOrDefaultAsync<string?>("uspUpdateChartOfAccount", new
        {
            iUserId = portfolioId,
            iAccountID = accountId,
            iAccountNumber = input.Number!.Value,
            strAccountName = input.Name.Trim(),
            strAccountType = input.Type,
            iParentAccountID = input.ParentId,
        }, commandType: CommandType.StoredProcedure);
        return Result(result);
    }

    /// <summary>Deactivates or reactivates one of the portfolio's own accounts (uspSetChartOfAccountActive).</summary>
    public async Task<AccountResult> SetActiveAsync(int portfolioId, int accountId, bool active)
    {
        var accounts = await GetAccountsAsync(portfolioId);
        if (OwnAccount(accounts, portfolioId, accountId) is { } refusal)
            return refusal;

        await using var con = db.Create();
        var result = await con.QuerySingleOrDefaultAsync<string?>("uspSetChartOfAccountActive",
            new { iUserId = portfolioId, iAccountID = accountId, bActive = active },
            commandType: CommandType.StoredProcedure);
        return Result(result);
    }

    /// <summary>
    /// Deletes one of the portfolio's own accounts with uspDeleteChartOfAccount, which refuses one
    /// with transactions or sub-accounts.
    /// </summary>
    public async Task<AccountResult> DeleteAsync(int portfolioId, int accountId)
    {
        var accounts = await GetAccountsAsync(portfolioId);
        if (OwnAccount(accounts, portfolioId, accountId) is { } refusal)
            return refusal;

        await using var con = db.Create();
        var result = await con.QuerySingleOrDefaultAsync<string?>("uspDeleteChartOfAccount",
            new { iUserId = portfolioId, iAccountID = accountId },
            commandType: CommandType.StoredProcedure);
        return Result(result);
    }

    /// <summary>Null when the account is one of the portfolio's own; otherwise the reason it can't be changed.</summary>
    public static AccountResult? OwnAccount(IReadOnlyList<LedgerAccount> accounts, int portfolioId, int accountId)
    {
        var account = accounts.FirstOrDefault(a => a.AccountId == accountId);
        if (account is null || (!account.IsSystem && account.OwnerId != portfolioId))
            return new AccountResult("That account wasn't found in this portfolio.");
        if (account.IsSystem)
            return new AccountResult("System accounts are shared by every portfolio and can't be changed.");
        return null;
    }

    /// <summary>The procs answer 'Success' or a message starting 'Error:'.</summary>
    public static AccountResult Result(string? procResult)
    {
        var text = procResult?.Trim() ?? "";
        if (text.Equals("Success", StringComparison.OrdinalIgnoreCase))
            return AccountResult.Success;
        if (text.StartsWith("Error:", StringComparison.OrdinalIgnoreCase))
            text = text["Error:".Length..].Trim();
        return new AccountResult(text.Length > 0 ? text : "The change wasn't saved.");
    }
}
