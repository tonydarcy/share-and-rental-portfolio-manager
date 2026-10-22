using System.Globalization;
using System.Text.RegularExpressions;

namespace PortfolioManager.Core.Imports;

public sealed record SharePriceRow(string StockCode, DateTime Date, decimal Open, decimal High, decimal Low, decimal Close, decimal Volume);

/// <summary>
/// End-of-day price files as the legacy Default page reads them: CommSec "Stock Easy" or
/// End of Day Data "EzyCharts" format, one line per security with no header:
/// Code, Date (yyyyMMdd or yyMMdd), Open, High, Low, Close, Volume.
/// </summary>
public static partial class SharePriceCsv
{
    public const int FieldCount = 7;

    [GeneratedRegex("^[A-Za-z0-9.]{1,10}$")]
    private static partial Regex ExchangeCodePattern();

    public static string? CheckExchangeCode(string? exchangeCode) =>
        exchangeCode is not null && ExchangeCodePattern().IsMatch(exchangeCode.Trim())
            ? null
            : "Enter the stock exchange code (letters and numbers, e.g. ASX).";

    public static ImportPreview<SharePriceRow> Parse(string text)
    {
        var lines = new List<ParsedLine<SharePriceRow>>();
        var seen = new HashSet<(string, DateTime)>();

        foreach (var (number, line) in CsvImport.Lines(text))
        {
            if (lines.Count >= CsvImport.MaxLines)
                return ImportPreview<SharePriceRow>.Failed($"The file has more than {CsvImport.MaxLines:N0} lines; split it into smaller files.");

            var fields = CsvImport.Split(line);
            var errors = new List<string>();
            SharePriceRow? row = null;

            if (fields.Count != FieldCount)
            {
                // A header line is fine to skip
                if (lines.Count == 0 && fields.Count > 1 && !CsvImport.TryNumber(fields[^1], out _))
                    continue;
                errors.Add($"Expected {FieldCount} comma separated values but found {fields.Count}.");
            }
            else
            {
                var code = fields[0].ToUpperInvariant();
                if (code.Length == 0 || code.Length > 50)
                    errors.Add("The stock code is missing or too long.");

                var date = ParseDate(fields[1]);
                if (date is null)
                {
                    if (lines.Count == 0 && !CsvImport.TryNumber(fields[5], out _))
                        continue;
                    errors.Add($"'{fields[1]}' isn't a date in yyyyMMdd or yyMMdd format.");
                }

                if (!CsvImport.TryNumber(fields[5], out var close) || close <= 0)
                    errors.Add("The close price must be a number above zero.");

                // Like the legacy upload, other values that aren't numbers are stored as zero
                var open = NumberOrZero(fields[2]);
                var high = NumberOrZero(fields[3]);
                var low = NumberOrZero(fields[4]);
                var volume = NumberOrZero(fields[6]);
                if (open < 0 || high < 0 || low < 0 || volume < 0)
                    errors.Add("Prices and volume can't be negative.");

                if (errors.Count == 0 && !seen.Add((code, date!.Value)))
                    errors.Add($"{code} on {date:d MMM yyyy} appears more than once in the file.");

                if (errors.Count == 0)
                    row = new SharePriceRow(code, date!.Value, open, high, low, close, volume);
            }

            lines.Add(new ParsedLine<SharePriceRow>(number, line, row, errors, []));
        }

        return lines.Count == 0 ? ImportPreview<SharePriceRow>.Failed("The file has no price lines.") : new(lines);
    }

    /// <summary>yyyyMMdd (CommSec) or yyMMdd (End of Day Data, taken as 20yy).</summary>
    public static DateTime? ParseDate(string s)
    {
        var format = s.Length switch { 8 => "yyyyMMdd", 6 => "yyMMdd", _ => null };
        if (format is null || !s.All(char.IsAsciiDigit))
            return null;
        if (format == "yyMMdd")
        {
            s = "20" + s;
            format = "yyyyMMdd";
        }
        return DateTime.TryParseExact(s, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
               && d.Year is >= 1900 and <= 2078
            ? d
            : null;
    }

    private static decimal NumberOrZero(string s) => CsvImport.TryNumber(s, out var v) ? v : 0m;
}
