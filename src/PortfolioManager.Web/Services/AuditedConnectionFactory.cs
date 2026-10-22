using System.Data;
using System.Data.Common;
using System.Security.Claims;
using Microsoft.Data.SqlClient;
using PortfolioManager.Core.Data;

namespace PortfolioManager.Web.Services;

/// <summary>
/// Opens connections as <see cref="SqlConnectionFactory"/> does, and tells the database which login is making the
/// request (SESSION_CONTEXT pm_user), so the audit trail triggers can record who changed what. Pooled connections
/// have it cleared when they are reused, so one person's id is never left on another's request.
/// </summary>
public sealed class AuditedConnectionFactory(string connectionString, IHttpContextAccessor http) : IDbConnectionFactory
{
    public DbConnection Create()
    {
        var con = new SqlConnection(connectionString);
        if (http.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier) is { } claim && int.TryParse(claim, out var userId))
            con.StateChange += (_, e) =>
            {
                if (e.CurrentState != ConnectionState.Open)
                    return;
                using var cmd = con.CreateCommand();
                cmd.CommandText = "EXEC sp_set_session_context N'pm_user', @id";
                cmd.Parameters.AddWithValue("@id", userId);
                cmd.ExecuteNonQuery();
            };
        return con;
    }
}
