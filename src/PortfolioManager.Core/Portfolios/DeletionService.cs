using System.Data;
using Dapper;
using PortfolioManager.Core.Data;

namespace PortfolioManager.Core.Portfolios;

/// <summary>A part of a portfolio that can be deleted on its own (the @strPart of uspDeletePortfolioData).</summary>
public enum DataPart { Trades, Payouts, Cash, Journals, Prices, All }

/// <summary>How many records each part of a portfolio holds, shown before anything is deleted.</summary>
public sealed record PortfolioDataCounts(int Trades, int Payouts, int Cash, int Journals, int Prices, DateTime? LockedTo)
{
    public int Of(DataPart part) => part switch
    {
        DataPart.Trades => Trades,
        DataPart.Payouts => Payouts,
        DataPart.Cash => Cash,
        DataPart.Journals => Journals,
        DataPart.Prices => Prices,
        _ => Trades + Payouts + Cash + Journals + Prices,
    };
}

/// <summary>A portfolio an account created, and what deleting the account does to it.</summary>
public sealed record AccountPortfolioPlan(int PortfolioId, string Name, int OthersWithAccess, int OtherOwners)
{
    /// <summary>Nobody else has access, so it goes with the account.</summary>
    public bool WillBeDeleted => OthersWithAccess == 0;

    /// <summary>Kept for others, none of whom is an owner, so the ones with the most access become owners.</summary>
    public bool OthersBecomeOwners => OthersWithAccess > 0 && OtherOwners == 0;
}

/// <summary>
/// Permanent deletes: one part of a portfolio, a whole portfolio, or an account. The procs check the signed-in
/// login owns the portfolio (fnCanDeletePortfolio), delete journals whole so the ledger still balances, and run in
/// one transaction each. Pages ask for the name to be typed first (<see cref="Confirms"/>).
/// </summary>
public sealed class DeletionService(IDbConnectionFactory db)
{
    // A whole portfolio or account touches every table, so allow longer than the default 30 seconds
    private const int LongTimeout = 300;

    /// <summary>What each part deletes, in plain words for the page.</summary>
    public static string Describe(DataPart part) => part switch
    {
        DataPart.Trades => "Every share trade, with its journal and the cash settlement recorded with it, and the corporate actions, " +
                           "splits, returns of capital, parcel choices and parcel transfers that depend on trades. Payouts stay. " +
                           "A Navexa sync brings the trades it added back on its next run.",
        DataPart.Payouts => "Every payout (dividends, distributions, interest and other income) with its journal, and the units " +
                            "dividend reinvestment bought. A Navexa sync brings the payouts it added back on its next run.",
        DataPart.Cash => "Every cash transaction with its journal, except the cash settlements of trades (those go with the trades). " +
                        "A Navexa sync brings the cash it added back on its next run.",
        DataPart.Journals => "Every journal in the ledger, bank reconciliations, currency revaluations and stock adjustments. " +
                             "Trades, payouts, cash, invoices and bills stay, but no longer appear in the accounts.",
        DataPart.Prices => "Share prices entered by hand or uploaded for this portfolio. Daily market prices aren't affected.",
        _ => "Everything recorded in the portfolio. Its name, settings, chart of accounts, tax codes, tracking categories, " +
             "custom groups, sharing, connections and audit trail stay, so you can start again. A Navexa sync " +
             "brings everything back on its next run. An old single-entry portfolio becomes double entry, like every new one.",
    };

    public static string Title(DataPart part) => part switch
    {
        DataPart.Trades => "All share trades",
        DataPart.Payouts => "All payouts (dividends and other income)",
        DataPart.Cash => "All cash transactions",
        DataPart.Journals => "All accounting journals",
        DataPart.Prices => "Prices entered by hand",
        _ => "Everything in the portfolio",
    };

    public static DataPart? ParsePart(string? text) =>
        Enum.TryParse<DataPart>(text?.Trim(), ignoreCase: true, out var part) && Enum.IsDefined(part) ? part : null;

    /// <summary>The typed confirmation matches the name, ignoring case and spaces at either end.</summary>
    public static bool Confirms(string? typed, string? expected) =>
        !string.IsNullOrWhiteSpace(expected)
        && string.Equals(typed?.Trim(), expected.Trim(), StringComparison.OrdinalIgnoreCase);

    public async Task<PortfolioDataCounts> GetCountsAsync(int portfolioId)
    {
        await using var con = db.Create();
        return await con.QuerySingleAsync<PortfolioDataCounts>("""
            SELECT (SELECT COUNT(*) FROM tblShareTrades WHERE iUserId = @portfolioId) AS Trades,
                   (SELECT COUNT(*) FROM tblPayouts WHERE iUserID = @portfolioId) AS Payouts,
                   (SELECT COUNT(*) FROM tblBankStatement WHERE iUserId = @portfolioId AND ISNULL(iShareTradeId, 0) = 0) AS Cash,
                   (SELECT COUNT(*) FROM tblJournal WHERE iUserID = @portfolioId) AS Journals,
                   (SELECT COUNT(*) FROM tblSharePrice WHERE iUserID = @portfolioId) AS Prices,
                   (SELECT MAX(dtmLockedTo) FROM tblLockedPeriod WHERE iUserID = @portfolioId) AS LockedTo
            """, new { portfolioId });
    }

    /// <summary>Whether a portfolio is the one an account signs in with (it can only go with the account).</summary>
    public async Task<bool> IsLoginPortfolioAsync(int portfolioId)
    {
        await using var con = db.Create();
        return await con.ExecuteScalarAsync<bool>("SELECT dbo.fnIsLoginRow(@portfolioId)", new { portfolioId });
    }

    public static object DataParameters(int userId, int portfolioId, DataPart part) =>
        new { iUserId = userId, iPortfolioId = portfolioId, strPart = part.ToString() };

    public async Task<ProcResult> DeleteDataAsync(int userId, int portfolioId, DataPart part)
    {
        await using var con = db.Create();
        var result = await con.ExecuteScalarAsync<string>("uspDeletePortfolioData", DataParameters(userId, portfolioId, part),
            commandType: CommandType.StoredProcedure, commandTimeout: LongTimeout);
        return ProcResult.From(result, "Deleted");
    }

    public static object PortfolioParameters(int userId, int portfolioId, string confirmName) =>
        new { iUserId = userId, iPortfolioId = portfolioId, strConfirm = confirmName.Trim() };

    public async Task<ProcResult> DeletePortfolioAsync(int userId, int portfolioId, string confirmName)
    {
        await using var con = db.Create();
        var result = await con.ExecuteScalarAsync<string>("uspDeletePortfolio", PortfolioParameters(userId, portfolioId, confirmName),
            commandType: CommandType.StoredProcedure, commandTimeout: LongTimeout);
        return ProcResult.From(result, "Portfolio Deleted");
    }

    private sealed record PlanRow(int PortfolioId, string? PortfolioName, int OthersWithAccess, int OtherOwners);

    /// <summary>The portfolios an account created and what happens to each when it is deleted (fnLoginDeletePlan).</summary>
    public async Task<IReadOnlyList<AccountPortfolioPlan>> GetAccountPlanAsync(int userId)
    {
        await using var con = db.Create();
        var rows = await con.QueryAsync<PlanRow>(
            "SELECT PortfolioId, PortfolioName, OthersWithAccess, OtherOwners FROM dbo.fnLoginDeletePlan(@userId) ORDER BY PortfolioId",
            new { userId });
        return rows.Select(r => new AccountPortfolioPlan(r.PortfolioId, r.PortfolioName?.Trim() ?? $"Portfolio {r.PortfolioId}",
            r.OthersWithAccess, r.OtherOwners)).ToList();
    }

    private sealed record AccountResult(string? Result, int Deleted, int Kept);

    /// <summary>Deletes the account. The page checks the password and typed email first.</summary>
    public async Task<(ProcResult Result, int Deleted, int Kept)> DeleteAccountAsync(int userId)
    {
        await using var con = db.Create();
        var row = await con.QuerySingleOrDefaultAsync<AccountResult>("uspDeleteLogin", new { iUserId = userId },
            commandType: CommandType.StoredProcedure, commandTimeout: LongTimeout);
        return (ProcResult.From(row?.Result, "Account Deleted"), row?.Deleted ?? 0, row?.Kept ?? 0);
    }
}
