using System.Globalization;

namespace PortfolioManager.Core.Accounting;

/// <summary>Reads proc rows that come back as dictionaries; a missing column or NULL reads as empty.</summary>
internal static class Db
{
    public static object? Value(IDictionary<string, object?> row, string column) =>
        row.TryGetValue(column, out var v) && v is not null and not DBNull ? v : null;

    public static string Str(IDictionary<string, object?> row, string column) =>
        Value(row, column) is { } v ? Convert.ToString(v, CultureInfo.InvariantCulture)!.Trim() : "";

    public static int? Int(IDictionary<string, object?> row, string column) =>
        Value(row, column) is { } v ? Convert.ToInt32(v, CultureInfo.InvariantCulture) : null;

    public static decimal? Dec(IDictionary<string, object?> row, string column) =>
        Value(row, column) is { } v ? Convert.ToDecimal(v, CultureInfo.InvariantCulture) : null;

    public static bool? Bool(IDictionary<string, object?> row, string column) =>
        Value(row, column) is { } v ? Convert.ToBoolean(v, CultureInfo.InvariantCulture) : null;

    public static DateOnly? Date(IDictionary<string, object?> row, string column) => Value(row, column) switch
    {
        DateTime d => DateOnly.FromDateTime(d),
        DateOnly d => d,
        string s when DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) => DateOnly.FromDateTime(d),
        _ => null,
    };

    public static DateTime? Timestamp(IDictionary<string, object?> row, string column) => Value(row, column) switch
    {
        DateTime d => d,
        string s when DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) => d,
        _ => null,
    };
}
