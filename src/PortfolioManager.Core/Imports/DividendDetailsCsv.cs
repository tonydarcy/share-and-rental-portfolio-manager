using System.Globalization;

namespace PortfolioManager.Core.Imports;

/// <summary>A dividend's franking and DRP price, for an announced dividend (or one to add, when the amount is given).</summary>
public sealed record DividendDetailsRow(string ExchangeCode, string StockCode, DateOnly ExDate, decimal? FrankedPercent, decimal? DrpPrice,
    decimal? AmountPerUnit, DateOnly? PaymentDate);

/// <summary>
/// The dividend details file: a header row, then Exchange, Code, Ex date, Franked %, DRP price, and optionally Amount
/// and Payment date. Franked % or DRP price can be blank, but not both.
/// </summary>
public static class DividendDetailsCsv
{
    public const string Header = "Exchange,Code,Ex date,Franked %,DRP price,Amount,Payment date";

    private static readonly string[] DateFormats = ["yyyy-MM-dd", "d/MM/yyyy", "dd/MM/yyyy", "d/M/yyyy", "d-MMM-yyyy", "dd-MMM-yyyy", "d MMM yyyy", "dd MMM yyyy", "yyyyMMdd"];

    public static ImportPreview<DividendDetailsRow> Parse(string text)
    {
        var lines = new List<ParsedLine<DividendDetailsRow>>();
        var seen = new HashSet<(string, string, DateOnly)>();
        foreach (var (number, line) in CsvImport.Lines(text))
        {
            if (lines.Count >= CsvImport.MaxLines)
                return ImportPreview<DividendDetailsRow>.Failed($"The file has more than {CsvImport.MaxLines:N0} lines; split it into smaller files.");
            var f = CsvImport.Split(line);
            // The header (or any first line with no date in the third column) is skipped
            if (lines.Count == 0 && (f.Count < 3 || Date(f[2]) is null) && f.Any(x => x.Contains("code", StringComparison.OrdinalIgnoreCase)))
                continue;

            var errors = new List<string>();
            DividendDetailsRow? row = null;
            if (f.Count < 5 || f.Count > 7)
                errors.Add("Expected Exchange, Code, Ex date, Franked %, DRP price, and optionally Amount and Payment date.");
            else
            {
                var exchange = f[0].Trim().ToUpperInvariant();
                var code = f[1].Trim().ToUpperInvariant();
                if (exchange.Length is 0 or > 50)
                    errors.Add("The exchange is missing or too long.");
                else if (Crypto.CryptoCodes.IsCrypto(exchange))
                    errors.Add("Crypto doesn't pay dividends, so it has no franking or DRP price.");
                if (code.Length is 0 or > 50)
                    errors.Add("The code is missing or too long.");
                var exDate = Date(f[2]);
                if (exDate is null)
                    errors.Add($"'{f[2]}' isn't a date (use e.g. 2026-08-20 or 20/08/2026).");
                var franked = Number(f[3].TrimEnd('%'), errors, "franked percentage");
                if (franked is < 0 or > 100)
                    errors.Add("The franked percentage must be between 0 and 100.");
                var drp = Number(f[4], errors, "DRP price");
                if (drp is <= 0)
                    errors.Add("The DRP price must be more than zero.");
                if (franked is null && drp is null)
                    errors.Add("Give the franked percentage, the DRP price or both.");
                var amount = f.Count > 5 ? Number(f[5], errors, "amount") : null;
                if (amount is <= 0)
                    errors.Add("The amount per share must be more than zero.");
                DateOnly? paid = null;
                if (f.Count > 6 && f[6].Trim().Length > 0 && (paid = Date(f[6])) is null)
                    errors.Add($"'{f[6]}' isn't a payment date.");
                if (errors.Count == 0 && !seen.Add((exchange, code, exDate!.Value)))
                    errors.Add($"{code} with ex-date {exDate:d MMM yyyy} appears more than once in the file.");
                if (errors.Count == 0)
                    row = new DividendDetailsRow(exchange, code, exDate!.Value, franked, drp, amount, paid);
            }
            lines.Add(new ParsedLine<DividendDetailsRow>(number, line, row, errors, []));
        }
        return lines.Count == 0 ? ImportPreview<DividendDetailsRow>.Failed("The file has no dividend lines.") : new(lines);
    }

    private static DateOnly? Date(string s) =>
        DateOnly.TryParseExact(s.Trim(), DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    private static decimal? Number(string s, List<string> errors, string label)
    {
        if (s.Trim().Length == 0)
            return null;
        if (CsvImport.TryNumber(s, out var n))
            return n;
        errors.Add($"The {label} '{s}' isn't a number.");
        return null;
    }
}
