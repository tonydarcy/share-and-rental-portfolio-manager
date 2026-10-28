using Dapper;

namespace PortfolioManager.Core.Data;

/// <summary>
/// Text parameters sent as varchar instead of Dapper's default nvarchar. Stock codes, exchange codes and FX pairs
/// are varchar columns, and with the database's SQL_Latin1 collation an nvarchar parameter makes SQL Server read
/// every row of tblSharePrice instead of seeking its index. Only needed in inline SQL: a proc converts its
/// parameters to the types it declares.
/// </summary>
public static class AnsiText
{
    public static DbString Of(string? value) => new() { Value = value, IsAnsi = true };

    /// <summary>For an <c>IN @list</c> parameter.</summary>
    public static DbString[] Of(IEnumerable<string> values) => values.Select(Of).ToArray();
}
