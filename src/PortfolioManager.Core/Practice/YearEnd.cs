using System.Data;
using Dapper;
using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Clients;
using PortfolioManager.Core.Data;
using PortfolioManager.Core.Statements;

namespace PortfolioManager.Core.Practice;

public static class YearStatuses
{
    public const string NotStarted = "NotStarted", InProgress = "InProgress", Review = "Review", Signed = "Signed", Locked = "Locked";

    /// <summary>The ones that can be chosen; Locked follows from the lock date.</summary>
    public static readonly IReadOnlyList<string> Choices = [NotStarted, InProgress, Review, Signed];

    public static string Label(string s) => s switch
    {
        NotStarted => "Not started",
        InProgress => "In progress",
        Review => "Ready for review",
        Signed => "Signed",
        Locked => "Locked",
        _ => s,
    };

    public static string Badge(string s) => s switch
    {
        InProgress => "text-bg-primary",
        Review => "text-bg-warning",
        Signed => "text-bg-success",
        Locked => "text-bg-dark",
        _ => "text-bg-light border",
    };

    /// <summary>A locked year shows as locked whatever was chosen.</summary>
    public static string Effective(string? chosen, bool locked) => locked ? Locked : chosen is { Length: > 0 } c && Choices.Contains(c) ? c : NotStarted;
}

public sealed record ChecklistItem(string Key, string Text, string? Page = null);

/// <summary>The year-end checklist for each entity type: the common steps, then the type's own.</summary>
public static class YearEndChecklist
{
    private static readonly ChecklistItem[] Common =
    [
        new("bank-rec", "Bank accounts reconciled to the year-end statements", "/Accounting/Reconcile"),
        new("unallocated", "No imported bank transactions left unallocated", "/Accounting/Unallocated"),
        new("holdings", "Holdings agree to the share registry or broker statements at year end", "/Holdings"),
        new("income", "Dividends, distributions and interest entered from the statements", "/Payouts/Index"),
        new("fund-statements", "Fund tax statements (AMMA/AMIT) entered for every managed fund and ETF", "/Payouts/FundStatements"),
        new("cgt", "Sales checked against contract notes and the capital gains reviewed", "/Reports/CapitalGains"),
        new("prefill", "ATO prefill compared with the ledger", "/TaxWork/Prefill"),
        new("queries", "Client queries answered and closed", "/Clients/Queries"),
        new("workpapers", "Workpapers prepared and reviewed for every balance", "/TaxWork/Workpapers"),
        new("statements", "Financial statements and report pack prepared", "/Accounting/ReportPack"),
        new("return", "Tax return prepared from the label schedule", "/TaxWork/ReturnLabels"),
        new("signed", "Statements and return signed by the client"),
        new("lock", "Year locked", "/Accounting/Lock"),
    ];

    private static readonly Dictionary<string, ChecklistItem[]> ByType = new()
    {
        [ClientEntityTypes.Individual] =
        [
            new("rental", "Rental property schedule completed", "/TaxWork/Rental"),
            new("deductions", "Investment deductions (interest, fees) checked"),
        ],
        [ClientEntityTypes.Company] =
        [
            new("div7a", "Division 7A loans checked and minimum repayments made", "/TaxWork/Div7a"),
            new("franking", "Franking account reconciled", "/TaxWork/CompanyTax"),
            new("solvency", "Directors' solvency resolution and minutes signed"),
            new("asic", "ASIC annual review fee paid and company details confirmed"),
        ],
        [ClientEntityTypes.Trust] =
        [
            new("distribution", "Trustee distribution resolution made by 30 June", "/TaxWork/Distribution"),
            new("tfn-report", "TFN report lodged for new beneficiaries"),
        ],
        [ClientEntityTypes.Partnership] =
        [
            new("partners", "Partners' shares of net income agreed"),
        ],
        [ClientEntityTypes.Smsf] =
        [
            new("members", "Member contributions, rollovers and benefits recorded", "/TaxWork/Smsf"),
            new("caps", "Contribution caps checked", "/TaxWork/Smsf"),
            new("pensions", "Minimum pensions paid", "/TaxWork/Smsf"),
            new("tbar", "Transfer balance account events reported", "/TaxWork/SmsfExtras"),
            new("ecpi", "Actuarial certificate obtained for exempt pension income (if needed)", "/TaxWork/SmsfExtras"),
            new("market-values", "Investments at market value at 30 June", "/Accounting/Revaluation"),
            new("strategy", "Investment strategy reviewed by the trustees", "/TaxWork/SmsfExtras"),
            new("audit", "Audit pack sent to the auditor and the audit report received", "/TaxWork/SmsfAudit"),
        ],
    };

    public static IReadOnlyList<ChecklistItem> For(string entityType) =>
        [.. Common.Take(Common.Length - 3), .. ByType.GetValueOrDefault(entityType, []), .. Common.Skip(Common.Length - 3)];
}

public sealed record ChecklistTick(string Key, bool Done, string? Note, string? DoneBy, DateTime? DoneAt);

/// <summary>A balance sheet account at year end with its workpaper, if one has been started.</summary>
public sealed record WorkpaperRow(int AccountId, int? Number, string Name, string Type, decimal Balance, Workpaper? Paper)
{
    public decimal? Difference => Paper?.Supported is { } s ? Balance - s : null;
    public bool Prepared => Paper?.Prepared is not null;
    public bool Reviewed => Paper?.Reviewed is not null;
}

public sealed record Workpaper(int Id, int AccountId, string? Note, decimal? Supported, string? PreparedBy, DateTime? Prepared,
    string? ReviewedBy, DateTime? Reviewed, string? ReviewNote);

public static class Workpapers
{
    /// <summary>Every asset, liability and equity account with a balance at year end, or a workpaper already, in account order.</summary>
    public static IReadOnlyList<WorkpaperRow> Rows(LedgerSnapshot yearEnd, IReadOnlyList<LedgerAccount> chart, IReadOnlyList<Workpaper> papers)
    {
        var byId = papers.ToDictionary(p => p.AccountId);
        return chart.Where(a => a.Type is "Asset" or "Liability" or "Equity" && a.Postable != false)
            .Select(a =>
            {
                var balance = yearEnd.Balance((a.Number, a.Name.Trim(), a.Type));
                return new WorkpaperRow(a.AccountId, a.Number, a.Name.Trim(), a.Type, balance, byId.GetValueOrDefault(a.AccountId));
            })
            .Where(r => r.Balance != 0 || r.Paper is not null)
            .OrderBy(r => r.Number)
            .ToList();
    }
}

/// <summary>Year-end status, checklist and workpapers (2026-10-21-16-practice.sql).</summary>
public sealed class YearEndService(IDbConnectionFactory db)
{
    public async Task<(string? Status, string? Note)> GetStatusAsync(int portfolioId, int year)
    {
        await using var con = db.Create();
        try
        {
            return await con.QuerySingleOrDefaultAsync<(string?, string?)>(
                "SELECT strStatus, strNote FROM tblYearStatus WHERE iUserID = @portfolioId AND iYear = @year", new { portfolioId, year });
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208)
        {
            return (null, null);
        }
    }

    public async Task<ProcResult> SaveStatusAsync(int portfolioId, int year, string status, string? note, int loginUserId)
    {
        await using var con = db.Create();
        return ProcResult.From(await con.ExecuteScalarAsync<string>("uspSaveYearStatus",
            new { iUserID = portfolioId, iYear = year, strStatus = status, strNote = note, iChangedByUserID = loginUserId },
            commandType: CommandType.StoredProcedure), "Success");
    }

    public async Task<IReadOnlyList<ChecklistTick>> GetChecklistAsync(int portfolioId, int year)
    {
        await using var con = db.Create();
        try
        {
            return (await con.QueryAsync<ChecklistTick>("""
                SELECT C.strItemKey AS [Key], C.bDone AS Done, C.strNote AS Note, U.strLoginEmail AS DoneBy, C.dtmDone AS DoneAt
                FROM tblYearChecklist C LEFT JOIN tblUsers U ON U.iUserId = C.iDoneByUserID
                WHERE C.iUserID = @portfolioId AND C.iYear = @year
                """, new { portfolioId, year })).ToList();
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208)
        {
            return [];
        }
    }

    public async Task<ProcResult> SaveChecklistItemAsync(int portfolioId, int year, string key, bool done, string? note, int loginUserId)
    {
        await using var con = db.Create();
        return ProcResult.From(await con.ExecuteScalarAsync<string>("uspSaveChecklistItem",
            new { iUserID = portfolioId, iYear = year, strItemKey = key, bDone = done, strNote = note, iDoneByUserID = loginUserId },
            commandType: CommandType.StoredProcedure), "Success");
    }

    public async Task<IReadOnlyList<Workpaper>> GetWorkpapersAsync(int portfolioId, int year)
    {
        await using var con = db.Create();
        try
        {
            return (await con.QueryAsync<Workpaper>("""
                SELECT W.iWorkpaperID AS Id, W.iAccountID AS AccountId, W.strNote AS Note, W.dblSupported AS Supported,
                       P.strLoginEmail AS PreparedBy, W.dtmPrepared AS Prepared, R.strLoginEmail AS ReviewedBy, W.dtmReviewed AS Reviewed,
                       W.strReviewNote AS ReviewNote
                FROM tblWorkpapers W
                LEFT JOIN tblUsers P ON P.iUserId = W.iPreparedByUserID
                LEFT JOIN tblUsers R ON R.iUserId = W.iReviewedByUserID
                WHERE W.iUserID = @portfolioId AND W.iYear = @year
                """, new { portfolioId, year })).ToList();
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208)
        {
            return [];
        }
    }

    public async Task<ProcResult> SaveWorkpaperAsync(int portfolioId, int year, int accountId, string? note, decimal? supported, int loginUserId)
    {
        await using var con = db.Create();
        var row = await con.QuerySingleAsync<(string Result, int? Id)>("uspSaveWorkpaper",
            new { iUserID = portfolioId, iYear = year, iAccountID = accountId, strNote = note, dblSupported = supported, iPreparedByUserID = loginUserId },
            commandType: CommandType.StoredProcedure);
        return ProcResult.From(row.Result, "Success", row.Id);
    }

    public async Task<ProcResult> ReviewWorkpaperAsync(int portfolioId, int workpaperId, bool signOff, string? note, int loginUserId)
    {
        await using var con = db.Create();
        return ProcResult.From(await con.ExecuteScalarAsync<string>("uspReviewWorkpaper",
            new { iUserID = portfolioId, iWorkpaperID = workpaperId, bSignOff = signOff, strReviewNote = note, iReviewedByUserID = loginUserId },
            commandType: CommandType.StoredProcedure), "Success");
    }
}
