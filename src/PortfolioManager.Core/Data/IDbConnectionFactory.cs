using System.Data.Common;
using Microsoft.Data.SqlClient;

namespace PortfolioManager.Core.Data;

public interface IDbConnectionFactory
{
    DbConnection Create();
}

/// <summary>Opens connections to the existing Portfolio Manager SQL Server database.</summary>
public sealed class SqlConnectionFactory(string connectionString) : IDbConnectionFactory
{
    public DbConnection Create() => new SqlConnection(connectionString);
}
