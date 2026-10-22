namespace PortfolioManager.Core.Tax;

internal static class Num
{
    // SUM() over no rows comes back NULL, and so does a missing price; both show as zero
    public static decimal Dec(IDictionary<string, object?> row, string column) => DecOrNull(row, column) ?? 0m;

    public static decimal? DecOrNull(IDictionary<string, object?> row, string column) =>
        row.TryGetValue(column, out var v) && v is not null and not DBNull ? Convert.ToDecimal(v) : null;
}
