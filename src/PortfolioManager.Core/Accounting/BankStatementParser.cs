using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace PortfolioManager.Core.Accounting;

/// <summary>One transaction read from a statement. Amount is positive for a deposit, negative for a withdrawal.</summary>
public sealed record BankTransaction(DateOnly Date, string Description, decimal Amount)
{
    public bool IsDeposit => Amount > 0;
}

/// <summary>One transaction of the file: parsed, or the reason it couldn't be.</summary>
public sealed record BankStatementLine(int Number, BankTransaction? Transaction, string? Error, bool Duplicate = false)
{
    public bool IsValid => Transaction is not null && Error is null;
}

public sealed record BankStatement(string Format, IReadOnlyList<BankStatementLine> Lines, string? FileError = null)
{
    public int ValidCount => Lines.Count(l => l.IsValid);
    public int ErrorCount => Lines.Count(l => !l.IsValid);
    public int DuplicateCount => Lines.Count(l => l.IsValid && l.Duplicate);
    public IEnumerable<BankTransaction> Transactions => Lines.Where(l => l.IsValid).Select(l => l.Transaction!);

    public static BankStatement Failed(string error) => new("", [], error);
}

/// <summary>
/// Reads QIF and OFX (also QFX) bank statements, as ImportBankStatement.aspx did:
/// QIF records end with ^ and use D (date), T or U (amount), P (payee), M (memo) and L (category);
/// OFX has a STMTTRN block per transaction with DTPOSTED, TRNAMT, NAME and MEMO.
/// </summary>
public static class BankStatementParser
{
    public const int MaxFileBytes = 2 * 1024 * 1024;
    public const int MaxTransactions = 5000;
    public const int MaxDescription = 500;

    public static readonly IReadOnlyList<string> Extensions = [".qif", ".ofx", ".qfx"];

    public static string? CheckFile(long length, string? fileName)
    {
        if (length <= 0)
            return "Choose a QIF or OFX statement file to upload.";
        if (length > MaxFileBytes)
            return $"The file is too large. The limit is {MaxFileBytes / (1024 * 1024)} MB; export a shorter period.";
        var ext = Path.GetExtension(fileName ?? "").ToLowerInvariant();
        if (!Extensions.Contains(ext))
            return "The file must be a .qif or .ofx (or .qfx) bank statement.";
        return null;
    }

    /// <summary>Banks still send Latin-1 files, so anything that isn't valid UTF-8 is read as Latin-1.</summary>
    public static string Decode(byte[] bytes)
    {
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        try
        {
            return utf8.GetString(bytes).TrimStart('﻿');
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    /// <summary>Works out the format from the content, falling back to the file extension.</summary>
    public static BankStatement Parse(string text, string? fileName = null)
    {
        var head = text.TrimStart('﻿', ' ', '\t', '\r', '\n');
        var ext = Path.GetExtension(fileName ?? "").ToLowerInvariant();
        if (head.StartsWith('!') || (ext == ".qif" && !LooksLikeOfx(head)))
            return ParseQif(text);
        if (LooksLikeOfx(head) || ext is ".ofx" or ".qfx")
            return ParseOfx(text);
        return BankStatement.Failed("This doesn't look like a QIF or OFX bank statement.");
    }

    private static bool LooksLikeOfx(string head) =>
        head.Contains("OFXHEADER", StringComparison.OrdinalIgnoreCase) || head.Contains("<OFX", StringComparison.OrdinalIgnoreCase);

    public static BankStatement ParseQif(string text)
    {
        var lines = new List<BankStatementLine>();
        var record = new List<string>();
        var skipping = false; // inside an !Account block, which describes the account, not a transaction

        void Finish()
        {
            if (!skipping && record.Count > 0)
            {
                var parsed = QifRecord(lines.Count + 1, record);
                if (parsed is not null)
                    lines.Add(parsed);
            }
            record.Clear();
        }

        foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
                continue;
            if (line.StartsWith('!'))
            {
                Finish();
                if (line.StartsWith("!Account", StringComparison.OrdinalIgnoreCase))
                    skipping = true;
                else if (line.StartsWith("!Type", StringComparison.OrdinalIgnoreCase))
                    skipping = false;
                continue;
            }
            if (line[0] == '^')
            {
                Finish();
                if (skipping)
                    skipping = false; // an !Account block holds one record
                continue;
            }
            record.Add(line);
            if (lines.Count > MaxTransactions)
                break;
        }
        Finish();

        return Result("QIF", lines);
    }

    /// <summary>One QIF record; null for a record with no date or amount (e.g. an opening header).</summary>
    private static BankStatementLine? QifRecord(int number, IReadOnlyList<string> fields)
    {
        string? date = null, amount = null;
        var parts = new List<string>();
        foreach (var f in fields)
        {
            var value = f[1..].Trim();
            switch (f[0])
            {
                case 'D':
                    date ??= value;
                    break;
                case 'T':
                case 'U':
                    amount ??= value;
                    break;
                case 'P':
                case 'M':
                case 'L':
                    if (value.Length > 0)
                        parts.Add(value);
                    break;
            }
        }

        if (date is null && amount is null)
            return null;
        return Build(number, date, amount, string.Join(" / ", parts));
    }

    private static readonly Regex OfxTransaction = new(@"<STMTTRN>(.*?)</STMTTRN>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static BankStatement ParseOfx(string text)
    {
        var lines = new List<BankStatementLine>();
        foreach (Match m in OfxTransaction.Matches(text))
        {
            var block = m.Groups[1].Value;
            var date = OfxValue(block, "DTPOSTED");
            var amount = OfxValue(block, "TRNAMT");
            var name = OfxValue(block, "NAME") ?? OfxValue(block, "PAYEE");
            var memo = OfxValue(block, "MEMO");
            var description = string.Equals(name, memo, StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(memo)
                ? name ?? ""
                : string.IsNullOrEmpty(name) ? memo : $"{name} {memo}";

            // DTPOSTED is yyyyMMdd, often followed by a time and time zone
            var day = date is { Length: >= 8 } ? date[..8] : date;
            lines.Add(Build(lines.Count + 1, day, amount, description));
            if (lines.Count > MaxTransactions)
                break;
        }

        if (lines.Count == 0 && !text.Contains("<OFX", StringComparison.OrdinalIgnoreCase))
            return BankStatement.Failed("This doesn't look like an OFX bank statement.");
        return Result("OFX", lines);
    }

    /// <summary>
    /// An element's value. OFX 1 (SGML) leaves elements unclosed, so the value runs to the next tag or line end.
    /// </summary>
    private static string? OfxValue(string block, string tag)
    {
        var m = Regex.Match(block, $@"<{tag}>([^<\r\n]*)", RegexOptions.IgnoreCase);
        if (!m.Success)
            return null;
        var value = WebUtility.HtmlDecode(m.Groups[1].Value).Trim();
        return value.Length == 0 ? null : value;
    }

    private static BankStatement Result(string format, List<BankStatementLine> lines)
    {
        if (lines.Count > MaxTransactions)
            return BankStatement.Failed($"The file has more than {MaxTransactions} transactions. Export a shorter period.");
        if (lines.Count == 0)
            return new BankStatement(format, lines, "No transactions were found in the file.");
        return new BankStatement(format, lines);
    }

    private static BankStatementLine Build(int number, string? date, string? amount, string description)
    {
        description = Regex.Replace(description, @"\s+", " ").Trim();
        if (description.Length > MaxDescription)
            description = description[..MaxDescription];

        var errors = new List<string>();
        DateOnly? d = null;
        if (string.IsNullOrWhiteSpace(date))
            errors.Add("No date.");
        else if (ParseDate(date) is { } parsed)
            d = parsed;
        else
            errors.Add($"The date \"{date}\" isn't one we recognise.");

        decimal? a = null;
        if (string.IsNullOrWhiteSpace(amount))
            errors.Add("No amount.");
        else if (ParseAmount(amount) is { } value)
        {
            if (value == 0)
                errors.Add("The amount is zero.");
            else
                a = value;
        }
        else
            errors.Add($"The amount \"{amount}\" isn't a number.");

        if (description.Length == 0 && d is not null && a is not null)
            description = a > 0 ? "Deposit" : "Withdrawal";

        return errors.Count > 0
            ? new BankStatementLine(number, null, string.Join(" ", errors))
            : new BankStatementLine(number, new BankTransaction(d!.Value, description, a!.Value), null);
    }

    private static readonly string[] DayFirst =
    [
        "d/M/yyyy", "d/M/yy", "d-M-yyyy", "d-M-yy", "d.M.yyyy", "d.M.yy",
        "yyyy-MM-dd", "yyyy/MM/dd", "yyyyMMdd", "d-MMM-yyyy", "d MMM yyyy", "d-MMM-yy", "d MMM yy",
    ];

    private static readonly string[] MonthFirst = ["M/d/yyyy", "M/d/yy"];

    /// <summary>
    /// Australian banks write the day first. Quicken's US style (10/25'26) is accepted when the day-first
    /// reading is impossible, as the legacy page did.
    /// </summary>
    public static DateOnly? ParseDate(string text)
    {
        var s = text.Trim().Replace('\'', '/').Replace(" /", "/").Replace("/ ", "/");
        s = Regex.Replace(s, @"\s+", " ");
        foreach (var formats in new[] { DayFirst, MonthFirst })
        {
            if (DateTime.TryParseExact(s, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) && d.Year is >= 1900 and <= 2100)
                return DateOnly.FromDateTime(d);
        }
        return null;
    }

    /// <summary>Amounts like -1,234.56, $50.00 or 50.00- ; a lone comma is a decimal comma (OFX allows it).</summary>
    public static decimal? ParseAmount(string text)
    {
        var s = text.Trim().Replace("$", "").Replace(" ", "");
        var negative = false;
        if (s.EndsWith('-'))
        {
            negative = true;
            s = s[..^1];
        }
        else if (s.StartsWith('(') && s.EndsWith(')'))
        {
            negative = true;
            s = s[1..^1];
        }
        if (s.Contains(',') && !s.Contains('.') && Regex.IsMatch(s, @",\d{1,2}$"))
            s = s.Replace(',', '.');
        else
            s = s.Replace(",", "");

        if (!decimal.TryParse(s, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
            return null;
        if (decimal.Round(value, 2) != value)
            return null;
        return negative ? -value : value;
    }

    /// <summary>The key two transactions share when they are the same line of a statement.</summary>
    public static (DateOnly Date, decimal Amount, string Description) Key(DateOnly date, decimal amount, string? description) =>
        (date, amount, Regex.Replace(description ?? "", @"\s+", " ").Trim().ToUpperInvariant());

    /// <summary>
    /// Marks transactions already imported into the same bank account. Each existing transaction
    /// accounts for one line only, so a file with two identical lines and one already imported
    /// flags just one of them.
    /// </summary>
    public static BankStatement FlagDuplicates(BankStatement statement, IEnumerable<(DateOnly Date, decimal Amount, string Description)> existing)
    {
        var counts = new Dictionary<(DateOnly, decimal, string), int>();
        foreach (var e in existing)
        {
            var key = Key(e.Date, e.Amount, e.Description);
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }

        var lines = statement.Lines.Select(l =>
        {
            if (!l.IsValid)
                return l;
            var t = l.Transaction!;
            var key = Key(t.Date, t.Amount, t.Description);
            if (counts.GetValueOrDefault(key) is var n && n > 0)
            {
                counts[key] = n - 1;
                return l with { Duplicate = true };
            }
            return l with { Duplicate = false };
        }).ToList();
        return statement with { Lines = lines };
    }
}
