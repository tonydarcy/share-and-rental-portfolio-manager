using System.Globalization;

namespace PortfolioManager.Core.Statements;

public static class StatementFormat
{
    private static readonly CultureInfo AU = CultureInfo.GetCultureInfo("en-AU");

    /// <summary>Accounting style: 1,234.56, negatives in brackets, a dash for nil, nothing for a heading.</summary>
    public static string Amount(decimal? value) => value switch
    {
        null => "",
        0 => "-",
        < 0 => "(" + (-value.Value).ToString("#,##0.00", AU) + ")",
        _ => value.Value.ToString("#,##0.00", AU),
    };

    /// <summary>$1,234.56 whatever the server's culture is.</summary>
    public static string Money(decimal value) => value.ToString("C", AU);

    /// <summary>30 June 2026.</summary>
    public static string Date(DateOnly date) => date.ToString("d MMMM yyyy", AU);

    /// <summary>Fixed years with a valid range, for the year picker.</summary>
    public static int ClampYear(int? year, int fallback) => year is >= 1902 and <= 9999 ? year.Value : fallback;
}
