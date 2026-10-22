using System.Data;
using Dapper;
using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Data;

namespace PortfolioManager.Core.Clients;

/// <summary>
/// Client details per portfolio and practice details per login, for the financial statements
/// (procs from 2026-10-15-financial-statements.sql).
/// </summary>
public sealed class ClientService(IDbConnectionFactory db)
{
    /// <summary>The portfolio's client details, or null if the portfolio doesn't exist.</summary>
    public async Task<ClientDetails?> GetAsync(int portfolioId)
    {
        await using var con = db.Create();
        await using var grid = await con.QueryMultipleAsync("uspGetClientDetails", new { iUserId = portfolioId },
            commandType: CommandType.StoredProcedure);
        var row = (await grid.ReadAsync()).Cast<IDictionary<string, object?>>().FirstOrDefault();
        var people = (await grid.ReadAsync()).Cast<IDictionary<string, object?>>().ToList();
        return row is null ? null : Map(row, people);
    }

    public static ClientDetails Map(IDictionary<string, object?> row, IEnumerable<IDictionary<string, object?>> officeholders)
    {
        var id = Db.Int(row, "iUserId") ?? 0;
        var portfolioName = Db.Str(row, "strEntityName");
        var portfolioType = Db.Str(row, "strPortfolioEntityType");
        if (Db.Bool(row, "bSaved") != true)
            return ClientDetails.Unsaved(id, portfolioName, portfolioType);

        var type = Db.Str(row, "strEntityType");
        return new ClientDetails(
            id,
            portfolioName,
            portfolioType,
            true,
            Db.Str(row, "strLegalName") is { Length: > 0 } legal ? legal : portfolioName,
            ClientEntityTypes.All.Contains(type) ? type : ClientEntityTypes.FromPortfolioType(portfolioType),
            Text(row, "strABN"),
            Text(row, "strACN"),
            Text(row, "strTrusteeName"),
            Text(row, "strTrusteeType"),
            Text(row, "strTrusteeACN"),
            new PostalAddress(Text(row, "strAddressLine1"), Text(row, "strAddressLine2"), Text(row, "strSuburb"),
                Text(row, "strState"), Text(row, "strPostcode")),
            Text(row, "strReportingBasis") ?? ClientDetails.DefaultReportingBasis,
            Db.Timestamp(row, "dtmUpdated"),
            officeholders
                .Select(o => new Officeholder(Db.Str(o, "strName"), Db.Str(o, "strRole")))
                .Where(o => o.Name.Length > 0)
                .ToList());
    }

    /// <summary>
    /// Saves the selected portfolio's client details and replaces its officeholders, in one transaction.
    /// The caller passes the portfolio from CurrentPortfolio and the signed-in login.
    /// </summary>
    public async Task<ProcResult> SaveAsync(int portfolioId, int loginUserId, ClientDetailsInput input)
    {
        var errors = input.Validate();
        if (errors.Count > 0)
            return ProcResult.Failed(errors[0]);

        await using var con = db.Create();
        await con.OpenAsync();
        await using var tx = await con.BeginTransactionAsync();

        var result = await con.QuerySingleOrDefaultAsync<string?>("uspSaveClientDetails", new
        {
            iUserId = portfolioId,
            strLegalName = input.LegalName.Trim(),
            strEntityType = input.EntityType,
            strABN = input.CleanAbn,
            strACN = input.CleanAcn,
            strTrusteeName = input.CleanTrusteeName,
            strTrusteeType = input.CleanTrusteeType,
            strTrusteeACN = input.CleanTrusteeAcn,
            strAddressLine1 = ClientDetailsInput.Blank(input.AddressLine1),
            strAddressLine2 = ClientDetailsInput.Blank(input.AddressLine2),
            strSuburb = ClientDetailsInput.Blank(input.Suburb),
            strState = ClientDetailsInput.Blank(input.State),
            strPostcode = ClientDetailsInput.Blank(input.Postcode),
            strReportingBasis = input.CleanReportingBasis,
            iUpdatedByUserID = loginUserId,
        }, tx, commandType: CommandType.StoredProcedure);

        var outcome = ProcResult.From(result, "Saved");
        if (!outcome.Ok)
        {
            await tx.RollbackAsync();
            return outcome;
        }

        await con.ExecuteAsync("uspDeleteClientOfficeholders", new { iUserId = portfolioId },
            tx, commandType: CommandType.StoredProcedure);
        var order = 0;
        foreach (var person in input.UsedOfficeholders)
        {
            await con.ExecuteAsync("uspAddClientOfficeholder", new
            {
                iUserId = portfolioId,
                strName = person.Name,
                strRole = person.Role,
                iSortOrder = ++order,
            }, tx, commandType: CommandType.StoredProcedure);
        }

        await tx.CommitAsync();
        return outcome;
    }

    /// <summary>
    /// The Clients page: the portfolios the login may open with their details. Only rows whose id
    /// is in <paramref name="accessibleIds"/> (from CurrentPortfolio) are kept.
    /// </summary>
    public async Task<IReadOnlyList<ClientListItem>> ListAsync(int loginUserId, IReadOnlyCollection<int> accessibleIds)
    {
        await using var con = db.Create();
        var rows = await con.QueryAsync("uspGetClientList", new { iUserId = loginUserId },
            commandType: CommandType.StoredProcedure);
        return MapList(rows.Cast<IDictionary<string, object?>>(), accessibleIds);
    }

    public static IReadOnlyList<ClientListItem> MapList(IEnumerable<IDictionary<string, object?>> rows, IReadOnlyCollection<int> accessibleIds) =>
        rows.Select(r => Map(r, []))
            .Where(d => accessibleIds.Contains(d.PortfolioId))
            .Select(d => new ClientListItem(d.PortfolioId, d.PortfolioName, d.PortfolioEntityType, d.Saved,
                d.LegalName, d.EntityType, d.Abn, d.Acn, d.Updated))
            .OrderBy(c => c.LegalName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    /// <summary>The login's practice details, or null until they are saved.</summary>
    public async Task<PracticeDetails?> GetPracticeAsync(int loginUserId)
    {
        await using var con = db.Create();
        var rows = await con.QueryAsync("uspGetPracticeDetails", new { iUserId = loginUserId },
            commandType: CommandType.StoredProcedure);
        return rows.Cast<IDictionary<string, object?>>().Select(MapPractice).FirstOrDefault();
    }

    public static PracticeDetails MapPractice(IDictionary<string, object?> row) => new(
        Db.Str(row, "strPracticeName"),
        Db.Str(row, "strPreparerName"),
        Text(row, "strTaxAgentNumber"),
        Text(row, "strProfessionalBody"),
        new PostalAddress(Text(row, "strAddressLine1"), Text(row, "strAddressLine2"), Text(row, "strSuburb"),
            Text(row, "strState"), Text(row, "strPostcode")),
        Text(row, "strPhone"),
        Text(row, "strEmail"));

    /// <summary>Saves the signed-in login's own practice details (never another login's).</summary>
    public async Task<ProcResult> SavePracticeAsync(int loginUserId, PracticeDetailsInput input)
    {
        var errors = input.Validate();
        if (errors.Count > 0)
            return ProcResult.Failed(errors[0]);

        await using var con = db.Create();
        var result = await con.QuerySingleOrDefaultAsync<string?>("uspSavePracticeDetails", new
        {
            iUserId = loginUserId,
            strPracticeName = input.PracticeName.Trim(),
            strPreparerName = input.PreparerName.Trim(),
            strTaxAgentNumber = input.CleanTaxAgentNumber,
            strProfessionalBody = ClientDetailsInput.Blank(input.ProfessionalBody),
            strAddressLine1 = ClientDetailsInput.Blank(input.AddressLine1),
            strAddressLine2 = ClientDetailsInput.Blank(input.AddressLine2),
            strSuburb = ClientDetailsInput.Blank(input.Suburb),
            strState = ClientDetailsInput.Blank(input.State),
            strPostcode = ClientDetailsInput.Blank(input.Postcode),
            strPhone = ClientDetailsInput.Blank(input.Phone),
            strEmail = ClientDetailsInput.Blank(input.Email),
        }, commandType: CommandType.StoredProcedure);
        return ProcResult.From(result, "Saved");
    }

    private static string? Text(IDictionary<string, object?> row, string column) =>
        Db.Str(row, column) is { Length: > 0 } s ? s : null;
}
