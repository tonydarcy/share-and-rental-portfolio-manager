using System.Data;
using Dapper;
using PortfolioManager.Core.Data;

namespace PortfolioManager.Core.Accounts;

/// <summary>Which version of the terms of use each login agreed to (tblTermsAcceptances).</summary>
public sealed class TermsService(IDbConnectionFactory db)
{
    public async Task RecordAsync(int userId, string version, string? ipAddress)
    {
        await using var con = db.Create();
        await con.ExecuteAsync("uspAddTermsAcceptance", new { iUserId = userId, strTermsVersion = version, strIpAddress = ipAddress },
            commandType: CommandType.StoredProcedure);
    }

    /// <summary>The newest version this login agreed to, or null if it never has.</summary>
    public async Task<string?> AcceptedVersionAsync(int userId)
    {
        await using var con = db.Create();
        return await con.QueryFirstOrDefaultAsync<string?>("uspGetTermsAcceptance", new { iUserId = userId },
            commandType: CommandType.StoredProcedure);
    }
}
