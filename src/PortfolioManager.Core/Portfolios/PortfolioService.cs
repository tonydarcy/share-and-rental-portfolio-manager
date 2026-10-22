using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using PortfolioManager.Core.Data;
using PortfolioManager.Core.Regions;

namespace PortfolioManager.Core.Portfolios;

/// <summary>
/// What someone may do with a portfolio. Full is an owner (view, change and share it), Edit an
/// editor (view and change, but not share), View read only.
/// </summary>
public enum PortfolioAccess { Full, View, Edit }

/// <summary>A portfolio the signed-in login may open. View access can look but not change anything.</summary>
public sealed record PortfolioSummary(int PortfolioId, string Name, string EntityType = "", PortfolioAccess Access = PortfolioAccess.Full)
{
    public bool CanEdit => Access != PortfolioAccess.View;

    /// <summary>Only owners change who else can open it.</summary>
    public bool CanShare => Access == PortfolioAccess.Full;
}

/// <summary>Someone given access to a portfolio (a tblPortfolioPermissions row).</summary>
public sealed record PortfolioShare(int PermissionId, int UserId, string Email, string Name, PortfolioAccess Access);

/// <summary>An invitation to someone who hasn't signed up yet; it becomes access when they first log in.</summary>
public sealed record PortfolioInvite(int InviteId, string Email, PortfolioAccess Access, DateTime Created);

/// <summary>A share that worked: Invited when the person hasn't signed up yet, so an invitation was saved.</summary>
public sealed record ShareResult(ProcResult Result, bool Invited = false)
{
    public static ShareResult From(string? procResult)
    {
        if (procResult?.Trim().StartsWith("Invitation Saved", StringComparison.OrdinalIgnoreCase) == true)
            return new ShareResult(ProcResult.Success(), Invited: true);
        return new ShareResult(ProcResult.From(procResult, "Portfolio Shared"));
    }
}

public sealed class PortfolioService(IDbConnectionFactory db)
{
    private sealed record AccessRow(int PortfolioId, string? PortfolioName, string? EntityType, string? Access);

    private sealed record ShareRow(int iPortfolioPermissionsID, int iUserID, string? LoginEmail, string? ContactName, string? Access);

    private sealed record InviteRow(int iInviteID, string? strEmail, string? strAccess, DateTime dtmCreatedDate);

    /// <summary>The portfolios a signed-in user may open, with their access (uspGetAccessiblePortfolios).</summary>
    public async Task<IReadOnlyList<PortfolioSummary>> GetAccessibleAsync(int userId)
    {
        await using var con = db.Create();
        var rows = await con.QueryAsync<AccessRow>("uspGetAccessiblePortfolios",
            new { iUserId = userId }, commandType: CommandType.StoredProcedure);

        return rows
            .Select(r => new PortfolioSummary(
                r.PortfolioId,
                r.PortfolioName?.Trim() is { Length: > 0 } name ? name : $"Portfolio {r.PortfolioId}",
                r.EntityType?.Trim() ?? "",
                ParseAccess(r.Access)))
            .ToList();
    }

    /// <summary>Who has been given access to a portfolio. Empty unless userId has full access to it.</summary>
    public async Task<IReadOnlyList<PortfolioShare>> GetSharesAsync(int userId, int portfolioId)
    {
        await using var con = db.Create();
        var rows = await con.QueryAsync<ShareRow>("uspGetPortfolioShares",
            new { iUserId = userId, iPortfolioId = portfolioId }, commandType: CommandType.StoredProcedure);
        return rows
            .Select(r => new PortfolioShare(r.iPortfolioPermissionsID, r.iUserID, r.LoginEmail?.Trim() ?? "", r.ContactName?.Trim() ?? "", ParseAccess(r.Access)))
            .ToList();
    }

    /// <summary>
    /// Gives the login registered with the email owner, editor or read only access, or changes the
    /// access it has. When nobody has signed up with the email, saves an invitation instead.
    /// </summary>
    public async Task<ShareResult> ShareAsync(int userId, int portfolioId, string email, PortfolioAccess access)
    {
        await using var con = db.Create();
        var result = await con.ExecuteScalarAsync<string>("uspSharePortfolio", new
        {
            iUserId = userId,
            iPortfolioId = portfolioId,
            strLoginEmail = email.Trim(),
            strAccess = AccessText(access),
        }, commandType: CommandType.StoredProcedure);
        return ShareResult.From(result);
    }

    /// <summary>Invitations not yet taken up. Empty unless userId may share the portfolio.</summary>
    public async Task<IReadOnlyList<PortfolioInvite>> GetInvitesAsync(int userId, int portfolioId)
    {
        await using var con = db.Create();
        var rows = await con.QueryAsync<InviteRow>("uspGetPortfolioInvites",
            new { iUserId = userId, iPortfolioId = portfolioId }, commandType: CommandType.StoredProcedure);
        return rows.Select(r => new PortfolioInvite(r.iInviteID, r.strEmail?.Trim() ?? "", ParseAccess(r.strAccess), r.dtmCreatedDate)).ToList();
    }

    public async Task<ProcResult> CancelInviteAsync(int userId, int portfolioId, int inviteId)
    {
        await using var con = db.Create();
        var result = await con.ExecuteScalarAsync<string>("uspCancelPortfolioInvite",
            new { iUserId = userId, iPortfolioId = portfolioId, iInviteID = inviteId },
            commandType: CommandType.StoredProcedure);
        return ProcResult.From(result, "Invitation Cancelled");
    }

    /// <summary>
    /// Turns invitations to the login's email into access (uspAcceptPortfolioInvites), when they log
    /// in. Returns how many portfolios were added; 0 before the shared access migration has run.
    /// </summary>
    public async Task<int> AcceptInvitesAsync(int userId)
    {
        await using var con = db.Create();
        try
        {
            return await con.ExecuteScalarAsync<int?>("uspAcceptPortfolioInvites", new { iUserId = userId },
                commandType: CommandType.StoredProcedure) ?? 0;
        }
        catch (SqlException ex) when (ex.Number == 2812) // stored procedure not found
        {
            return 0;
        }
    }

    public static string AccessText(PortfolioAccess access) => access switch
    {
        PortfolioAccess.View => "View",
        PortfolioAccess.Edit => "Edit",
        _ => "Admin",
    };

    /// <summary>The words shown for each access level.</summary>
    public static string AccessLabel(PortfolioAccess access) => access switch
    {
        PortfolioAccess.View => "Read only",
        PortfolioAccess.Edit => "Editor",
        _ => "Owner",
    };

    public async Task<ProcResult> RemoveShareAsync(int userId, int portfolioId, int permissionId)
    {
        await using var con = db.Create();
        var result = await con.ExecuteScalarAsync<string>("uspRemovePortfolioShare",
            new { iUserId = userId, iPortfolioId = portfolioId, iPortfolioPermissionsID = permissionId },
            commandType: CommandType.StoredProcedure);
        return ProcResult.From(result, "Access Removed");
    }

    /// <summary>
    /// The access text in tblPortfolioPermissions. The old site only wrote 'Admin', and treated any row
    /// as full access, so only the look-only words mean View and only Edit means an editor.
    /// </summary>
    public static PortfolioAccess ParseAccess(string? text) =>
        (text ?? "").Trim().ToUpperInvariant().Replace("-", " ").Replace(" ", "") switch
        {
            "VIEW" or "VIEWONLY" or "READ" or "READONLY" => PortfolioAccess.View,
            "EDIT" or "EDITOR" => PortfolioAccess.Edit,
            _ => PortfolioAccess.Full,
        };

    public static string? ValidateShare(string? email, string? access)
    {
        var e = email?.Trim() ?? "";
        if (e.Length == 0)
            return "Enter the email address they log in with.";
        if (e.Length > 500 || !e.Contains('@'))
            return "Check the email address.";
        if (ParseChoice(access) is null)
            return "Choose what they can do.";
        return null;
    }

    /// <summary>The access radio buttons on the Sharing page: "View", "Edit" or "Full".</summary>
    public static PortfolioAccess? ParseChoice(string? choice) => choice switch
    {
        "View" => PortfolioAccess.View,
        "Edit" => PortfolioAccess.Edit,
        "Full" => PortfolioAccess.Full,
        _ => null,
    };

    private sealed record RegionRow(string? strCountryCode, int? iYearStartMonth, int? iYearStartDay, string? strPortfolioCurrency, string? strNumberFormat);

    /// <summary>
    /// The portfolio's country, year start, currency and format (uspGetPortfolioRegion). Australia's
    /// settings until the region migration has been run, so the site still works before it.
    /// </summary>
    public async Task<RegionSettings> GetRegionAsync(int portfolioId)
    {
        await using var con = db.Create();
        try
        {
            var row = await con.QueryFirstOrDefaultAsync<RegionRow>("uspGetPortfolioRegion",
                new { iPortfolioId = portfolioId }, commandType: CommandType.StoredProcedure);
            return row is null
                ? RegionSettings.Australia
                : RegionSettings.From(row.strCountryCode, row.iYearStartMonth, row.iYearStartDay, row.strPortfolioCurrency, row.strNumberFormat);
        }
        catch (SqlException ex) when (ex.Number == 2812) // stored procedure not found
        {
            return RegionSettings.Australia;
        }
    }

    /// <summary>Saves the portfolio's region settings. The proc checks userId has full access to it.</summary>
    public async Task<ProcResult> SaveRegionAsync(int userId, int portfolioId, RegionSettings settings)
    {
        await using var con = db.Create();
        var result = await con.ExecuteScalarAsync<string>("uspSavePortfolioRegion", RegionParameters(userId, portfolioId, settings),
            commandType: CommandType.StoredProcedure);
        return ProcResult.From(result, "Settings Saved");
    }

    public static object RegionParameters(int userId, int portfolioId, RegionSettings s) => new
    {
        iUserId = userId,
        iPortfolioId = portfolioId,
        strCountryCode = s.CountryCode,
        iYearStartMonth = s.YearStart.Month,
        iYearStartDay = s.YearStart.Day,
        strPortfolioCurrency = s.Currency,
        strNumberFormat = s.Format,
    };

    /// <summary>The entity types the legacy site offers for a portfolio.</summary>
    public static readonly IReadOnlyList<string> EntityTypes = ["Individual", "Company", "Superfund", "Other"];

    public static IReadOnlyList<string> ValidateNew(string? name, string? entityType)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(name))
            errors.Add("Enter a name for the portfolio.");
        else if (name.Trim().Length > 100)
            errors.Add("The portfolio name can be at most 100 characters.");
        if (!EntityTypes.Contains(entityType ?? ""))
            errors.Add("Choose an entity type.");
        return errors;
    }

    /// <summary>
    /// Creates a portfolio owned by the signed-in user with AddNewPortfolio, which also gives them
    /// Admin access to it in tblPortfolioPermissions. New portfolios use double-entry accounting (the proc's default).
    /// </summary>
    public async Task CreateAsync(int ownerUserId, string name, string entityType)
    {
        await using var con = db.Create();
        await con.ExecuteAsync("AddNewPortfolio",
            new { iUserID = ownerUserId, strEntityName = name.Trim(), strEntityType = entityType },
            commandType: CommandType.StoredProcedure);
    }

    /// <summary>
    /// Picks which portfolio to show: the requested one if the user may open it,
    /// otherwise their own, otherwise the first they can open. Null when they can open none.
    /// </summary>
    public static PortfolioSummary? Choose(IReadOnlyList<PortfolioSummary> accessible, int userId, int? requestedId) =>
        accessible.FirstOrDefault(p => p.PortfolioId == requestedId)
        ?? accessible.FirstOrDefault(p => p.PortfolioId == userId)
        ?? accessible.FirstOrDefault();
}
