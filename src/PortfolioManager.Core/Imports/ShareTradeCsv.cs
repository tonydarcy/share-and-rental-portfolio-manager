using System.Globalization;

namespace PortfolioManager.Core.Imports;

public sealed record ShareTradeRow(
    string StockExchangeCode,
    string StockCode,
    string Name,
    string Type,
    DateTime Date,
    decimal Quantity,
    decimal Price,
    decimal Brokerage,
    string Currency,
    decimal ExchangeRate,
    string Comment)
{
    /// <summary>Identifies a trade for spotting ones already on file.</summary>
    public string Key => TradeKey(StockExchangeCode, StockCode, Type, Date, Quantity, Price);

    public static string TradeKey(string exchange, string code, string type, DateTime date, decimal quantity, decimal price) =>
        string.Join('|', exchange.Trim().ToUpperInvariant(), code.Trim().ToUpperInvariant(), type.Trim().ToUpperInvariant(),
            date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Math.Round(quantity, 8).ToString("0.########", CultureInfo.InvariantCulture),
            Math.Round(price, 4).ToString("0.####", CultureInfo.InvariantCulture));
}

/// <summary>
/// The share trades upload file (see samples/share-trades.csv), no header:
/// Exchange, Code, Name, Buy/Sell, Date, Quantity, Price per unit, Brokerage, Currency, Exchange rate, Comment.
/// </summary>
public static class ShareTradeCsv
{
    public const int FieldCount = 11;

    public static readonly string[] DateFormats =
        ["yyyy-MM-dd", "d-MMM-yyyy", "dd-MMM-yyyy", "d MMM yyyy", "d/MM/yyyy", "d/M/yyyy", "dd/MM/yyyy"];

    /// <summary>A blank currency means defaultCurrency, the portfolio's currency.</summary>
    public static ImportPreview<ShareTradeRow> Parse(string text, DateTime today, string defaultCurrency = "AUD")
    {
        var lines = new List<ParsedLine<ShareTradeRow>>();

        foreach (var (number, line) in CsvImport.Lines(text))
        {
            if (lines.Count >= CsvImport.MaxLines)
                return ImportPreview<ShareTradeRow>.Failed($"The file has more than {CsvImport.MaxLines:N0} lines; split it into smaller files.");

            var f = CsvImport.Split(line);
            var errors = new List<string>();
            ShareTradeRow? row = null;

            if (f.Count != FieldCount)
            {
                errors.Add($"Expected {FieldCount} comma separated values but found {f.Count}.");
            }
            else
            {
                // A header line (no number in the quantity column) is skipped
                if (lines.Count == 0 && !CsvImport.TryNumber(f[5], out _) && !IsTradeType(f[3]))
                    continue;

                var exchange = f[0].ToUpperInvariant();
                var code = f[1].ToUpperInvariant();
                if (exchange.Length is 0 or > 50)
                    errors.Add("The exchange is missing or too long.");
                if (code.Length is 0 or > 50)
                    errors.Add("The stock code is missing or too long.");
                if (f[2].Length > 500)
                    errors.Add("The name is too long.");

                string? type = null;
                if (IsTradeType(f[3]))
                    type = f[3].Equals("buy", StringComparison.OrdinalIgnoreCase) ? "Buy" : "Sell";
                else
                    errors.Add($"The type must be Buy or Sell, not '{f[3]}'.");

                DateTime? date = null;
                if (DateTime.TryParseExact(f[4], DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                {
                    if (d.Date > today.Date)
                        errors.Add("The trade date is in the future.");
                    else if (d.Year < 1900)
                        errors.Add("The trade date is too early.");
                    else
                        date = d.Date;
                }
                else
                    errors.Add($"'{f[4]}' isn't a date (use e.g. 2024-02-29 or 29-Feb-2024).");

                decimal quantity = 0;
                if (!CsvImport.TryNumber(f[5], out var q))
                    errors.Add("The quantity must be a number above zero.");
                else if (Validation.FormRules.UnitsProblem(q) is { } problem)
                    errors.Add(problem);
                else
                    quantity = q;

                if (!CsvImport.TryNumber(f[6], out var price) || price < 0)
                    errors.Add("The price must be a number (zero or more).");

                decimal brokerage = 0;
                if (f[7].Length > 0 && (!CsvImport.TryNumber(f[7], out brokerage) || brokerage < 0))
                    errors.Add("The brokerage must be a number (zero or more).");

                var currency = f[8].Length == 0 ? defaultCurrency : f[8].ToUpperInvariant();
                if (currency.Length != 3 || !currency.All(char.IsAsciiLetter))
                    errors.Add("The currency must be a three letter code such as AUD.");

                decimal rate = 1;
                if (f[9].Length > 0 && (!CsvImport.TryNumber(f[9], out rate) || rate <= 0))
                    errors.Add("The exchange rate must be a number above zero.");

                if (f[10].Length > 500)
                    errors.Add("The comment is too long (500 characters at most).");

                if (errors.Count == 0)
                    row = new ShareTradeRow(exchange, code, f[2], type!, date!.Value, quantity, price, brokerage, currency, rate, f[10]);
            }

            lines.Add(new ParsedLine<ShareTradeRow>(number, line, row, errors, []));
        }

        return lines.Count == 0 ? ImportPreview<ShareTradeRow>.Failed("The file has no trades in it.") : new(lines);
    }

    /// <summary>Adds a warning to each valid line that matches a trade already on file (or an earlier line).</summary>
    public static ImportPreview<ShareTradeRow> FlagDuplicates(ImportPreview<ShareTradeRow> preview, IReadOnlySet<string> existingKeys)
    {
        var inFile = new HashSet<string>();
        var lines = preview.Lines.Select(l =>
        {
            if (!l.IsValid)
                return l;
            var warnings = new List<string>(l.Warnings);
            if (existingKeys.Contains(l.Value!.Key))
                warnings.Add("A trade with the same code, type, date, quantity and price is already in this portfolio.");
            if (!inFile.Add(l.Value!.Key))
                warnings.Add("The same trade appears earlier in the file.");
            return l with { Warnings = warnings };
        }).ToList();
        return preview with { Lines = lines };
    }

    private static bool IsTradeType(string s) =>
        s.Equals("buy", StringComparison.OrdinalIgnoreCase) || s.Equals("sell", StringComparison.OrdinalIgnoreCase);
}
