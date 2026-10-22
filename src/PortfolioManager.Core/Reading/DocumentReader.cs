using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Anthropic;
using Anthropic.Models.Messages;
using UglyToad.PdfPig;

namespace PortfolioManager.Core.Reading;

public static class ReadKinds
{
    public const string Receipt = "Receipt", Bill = "Bill", Statement = "Statement", Trade = "Trade";
    public static readonly IReadOnlyList<string> All = [Receipt, Bill, Statement, Trade];
}

/// <summary>
/// What was read from a receipt, bill, statement or contract note. Every field is a suggestion: the person checks it
/// on the form it fills before anything is saved.
/// </summary>
public sealed record ReadResult(
    string Kind,
    string? Payee,
    DateOnly? Date,
    string? Reference,
    string? TaxNumber,
    string? Description,
    decimal? Total,
    decimal? Tax,
    string? Currency,
    DateOnly? DueDate = null,
    string? TradeCode = null,
    string? TradeSide = null,
    decimal? Units = null,
    decimal? Price = null,
    decimal? Brokerage = null,
    string Source = "Text",
    IReadOnlyList<string>? Notes = null)
{
    public bool IsEmpty => Payee is null && Date is null && Total is null && TradeCode is null;
}

/// <summary>Settings for the optional Claude reader. With no API key, only the built-in text reader is used.</summary>
public sealed record DocumentReadingOptions(string? AnthropicApiKey, string Model = "claude-opus-5-5")
{
    public bool UsesClaude => !string.IsNullOrWhiteSpace(AnthropicApiKey);
}

public static class DocumentText
{
    public const int MaxBytes = 10 * 1024 * 1024;

    public static bool IsPdf(string contentType) => contentType == "application/pdf";

    public static bool IsImage(string contentType) => contentType is "image/jpeg" or "image/png" or "image/gif" or "image/webp";

    /// <summary>The text in a PDF that has a text layer (most bills and statements emailed as PDFs). A scanned page has none.</summary>
    public static string FromPdf(byte[] data)
    {
        try
        {
            using var pdf = PdfDocument.Open(data);
            var text = new StringBuilder();
            foreach (var page in pdf.GetPages().Take(20))
            {
                // Words in reading order, a line break where the line changes
                double? lastY = null;
                foreach (var word in page.GetWords().OrderByDescending(w => Math.Round(w.BoundingBox.Bottom)).ThenBy(w => w.BoundingBox.Left))
                {
                    var y = Math.Round(word.BoundingBox.Bottom);
                    if (lastY is { } ly && Math.Abs(ly - y) > 2)
                        text.AppendLine();
                    else if (lastY is not null)
                        text.Append(' ');
                    text.Append(word.Text);
                    lastY = y;
                }
                text.AppendLine();
            }
            return text.ToString();
        }
        catch (Exception)
        {
            return "";
        }
    }
}

/// <summary>
/// Reads the common fields from a document's text with patterns: the total, the tax, the date, the invoice number, the
/// ABN, the business name, and for a contract note the code, units and price. It is a first guess; Claude does better.
/// </summary>
public static partial class TextReader
{
    public static ReadResult Read(string text, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        var lines = text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var notes = new List<string>();

        var trade = Trade().Match(text);
        if (trade.Success && Units().Match(text) is { Success: true } units)
        {
            var price = Price().Match(text);
            var brokerage = Brokerage().Match(text);
            return new ReadResult(ReadKinds.Trade, FirstName(lines), FindDate(text, culture), FindReference(text), FindAbn(text), null,
                FindAmount(text, TotalLabel()), null, null,
                TradeCode: trade.Groups["code"].Value.ToUpperInvariant(),
                TradeSide: trade.Groups["side"].Value.StartsWith("B", StringComparison.OrdinalIgnoreCase) ? "Buy" : "Sell",
                Units: Number(units.Groups["n"].Value),
                Price: price.Success ? Number(price.Groups["n"].Value) : null,
                Brokerage: brokerage.Success ? Number(brokerage.Groups["n"].Value) : null,
                Notes: ["Read as a contract note. Check the code, units, price and brokerage."]);
        }

        var total = FindAmount(text, TotalLabel());
        if (total is null)
        {
            // No "total" label: take the largest amount on the page
            var amounts = Amount().Matches(text).Select(m => Number(m.Groups["n"].Value)).Where(a => a is > 0).ToList();
            if (amounts.Count > 0)
            {
                total = amounts.Max();
                notes.Add("No total was labelled, so the largest amount was used.");
            }
        }
        var tax = FindAmount(text, TaxLabel());
        if (tax is { } t && total is { } g && t >= g)
            tax = null;
        var kind = StatementWords().IsMatch(text) ? ReadKinds.Statement
            : InvoiceWords().IsMatch(text) ? ReadKinds.Bill
            : ReadKinds.Receipt;
        var currency = Currency().Match(text) is { Success: true } c ? c.Groups["c"].Value.ToUpperInvariant() : null;
        var due = DueLabel().Match(text) is { Success: true } d ? ParseDate(d.Groups["d"].Value, culture) : null;
        return new ReadResult(kind, FirstName(lines), FindDate(text, culture), FindReference(text), FindAbn(text), null, total, tax, currency, due,
            Notes: notes);
    }

    private static decimal? FindAmount(string text, Regex label)
    {
        // The last labelled amount wins: "Subtotal" comes before "Total"
        decimal? found = null;
        foreach (Match m in label.Matches(text))
            if (Number(m.Groups["n"].Value) is { } n)
                found = n;
        return found;
    }

    private static string? FindReference(string text) => Reference().Match(text) is { Success: true } m ? m.Groups["r"].Value.Trim() : null;

    private static string? FindAbn(string text) => Abn().Match(text) is { Success: true } m ? Regex.Replace(m.Groups["a"].Value, @"\s", "") : null;

    private static DateOnly? FindDate(string text, CultureInfo culture)
    {
        foreach (Match m in DateText().Matches(text))
            if (ParseDate(m.Value, culture) is { } d)
                return d;
        return null;
    }

    public static DateOnly? ParseDate(string value, CultureInfo culture)
    {
        value = value.Trim().TrimEnd(',', '.');
        string[] formats = ["d/M/yyyy", "d/M/yy", "d-M-yyyy", "d.M.yyyy", "yyyy-MM-dd", "d MMM yyyy", "d MMMM yyyy", "MMM d yyyy", "MMMM d yyyy",
            "d-MMM-yyyy", "d MMM yy", "d-MMM-yy"];
        // Day first unless the portfolio's format is month first (en-US)
        if (culture.DateTimeFormat.ShortDatePattern.StartsWith('M'))
            formats = ["M/d/yyyy", "M/d/yy", .. formats];
        var cleaned = value.Replace(",", "");
        return DateOnly.TryParseExact(cleaned, formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var d) ? d : null;
    }

    private static string? FirstName(IEnumerable<string> lines) =>
        lines.Take(8).FirstOrDefault(l => l.Length is >= 3 and <= 80 && l.Any(char.IsLetter) && !NotAName().IsMatch(l));

    public static decimal? Number(string value) =>
        decimal.TryParse(value.Replace(",", "").Replace("$", "").Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var n) ? n : null;

    private const string Num = @"(?<n>\d{1,3}(?:,\d{3})*(?:\.\d{1,2})?|\d+(?:\.\d{1,2})?)";

    [GeneratedRegex(@"(?im)\b(?:total(?:\s+(?:amount|due|payable|inc(?:l|luding)?\.?\s*(?:gst|tax|vat)))?|amount\s+(?:due|payable|paid)|balance\s+due|grand\s+total)\b[^\d\n-]{0,25}-?\$?\s?" + Num)]
    private static partial Regex TotalLabel();

    [GeneratedRegex(@"(?im)\b(?:gst|vat|sales\s+tax|tax)\b(?!\s*(?:invoice|no|number|#|inclusive|incl))[^\d\n]{0,25}\$?\s?" + Num)]
    private static partial Regex TaxLabel();

    [GeneratedRegex(@"(?i)\$\s?" + Num)]
    private static partial Regex Amount();

    // On the label's line, and with a digit in it, so "Tax Invoice" on its own isn't read as a reference
    [GeneratedRegex(@"(?im)\b(?:invoice|tax\s+invoice|receipt|bill|reference|ref)[ \t]*(?:no\.?|number|#)?[ \t]*[:#]?[ \t]*(?<r>(?=[A-Z\-/]*\d)[A-Z0-9][A-Z0-9\-/]{2,30})\b")]
    private static partial Regex Reference();

    [GeneratedRegex(@"(?i)\bABN\s*:?\s*(?<a>\d{2}\s?\d{3}\s?\d{3}\s?\d{3})")]
    private static partial Regex Abn();

    [GeneratedRegex(@"(?i)\b(?:\d{1,2}[/\-.]\d{1,2}[/\-.]\d{2,4}|\d{4}-\d{2}-\d{2}|\d{1,2}[ \-](?:jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec)[a-z]*[ \-,]+\d{2,4}|(?:jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec)[a-z]* \d{1,2},? \d{4})\b")]
    private static partial Regex DateText();

    [GeneratedRegex(@"(?i)\b(?:due\s+date|due\s+by|payment\s+due|due)\s*:?\s*(?<d>\d{1,2}[/\-.]\d{1,2}[/\-.]\d{2,4}|\d{1,2}\s+[a-z]{3,9}\s+\d{4})")]
    private static partial Regex DueLabel();

    [GeneratedRegex(@"\b(?<c>USD|NZD|GBP|EUR|JPY|CAD|SGD|HKD|CHF)\b")]
    private static partial Regex Currency();

    [GeneratedRegex(@"(?i)\b(?:statement\s+of\s+account|account\s+statement|bank\s+statement|opening\s+balance|closing\s+balance)\b")]
    private static partial Regex StatementWords();

    [GeneratedRegex(@"(?i)\b(?:tax\s+invoice|invoice)\b")]
    private static partial Regex InvoiceWords();

    [GeneratedRegex(@"(?i)\b(?<side>bought|buy|sold|sell)\b[^\n]{0,60}?\b(?:ASX\s*:?\s*)?(?<code>[A-Z]{3,5})\b(?=[^\n]*(?:units|shares|qty|quantity|@))")]
    private static partial Regex Trade();

    [GeneratedRegex(@"(?i)\b(?:quantity|qty|units|shares)\b\s*:?\s*(?<n>\d{1,3}(?:,\d{3})*|\d+)")]
    private static partial Regex Units();

    [GeneratedRegex(@"(?i)(?:\bprice\b|@)\s*:?\s*\$?\s?(?<n>\d+(?:\.\d{1,4})?)")]
    private static partial Regex Price();

    [GeneratedRegex(@"(?i)\b(?:brokerage|commission)\b[^\d\n]{0,20}\$?\s?(?<n>\d+(?:\.\d{1,2})?)")]
    private static partial Regex Brokerage();

    [GeneratedRegex(@"(?i)^(?:tax\s+invoice|invoice|receipt|statement|page\s+\d|date|abn|phone|ph|email|www\.|http|\d)")]
    private static partial Regex NotAName();
}

/// <summary>
/// Reads an uploaded PDF or photo: Claude when an API key is set (it reads scans and photos too), otherwise the text in
/// the PDF with <see cref="TextReader"/>. A photo can only be read by Claude.
/// </summary>
public sealed class DocumentReadingService(DocumentReadingOptions options)
{
    public bool UsesClaude => options.UsesClaude;

    public async Task<(ReadResult? Result, string? Error)> ReadAsync(byte[] data, string contentType, CultureInfo culture, CancellationToken ct = default)
    {
        if (data.Length == 0 || data.Length > DocumentText.MaxBytes)
            return (null, "Choose a PDF or photo of up to 10 MB.");
        if (!DocumentText.IsPdf(contentType) && !DocumentText.IsImage(contentType))
            return (null, "Only PDFs and photos (JPEG, PNG, GIF or WebP) can be read.");

        string? claudeError = null;
        if (options.UsesClaude)
        {
            try
            {
                if (await ReadWithClaudeAsync(data, contentType, ct) is { } read)
                    return (read, null);
                claudeError = "Claude couldn't read it.";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                claudeError = "Claude couldn't be reached (" + ex.GetType().Name + ").";
            }
        }
        if (!DocumentText.IsPdf(contentType))
            return (null, claudeError ?? "Photos can only be read when a Claude API key is set up (see SETUP.md). Enter the details by hand.");
        var text = DocumentText.FromPdf(data);
        if (string.IsNullOrWhiteSpace(text))
            return (null, (claudeError is null ? "" : claudeError + " ") + "This PDF has no text in it (it may be a scan). Enter the details by hand.");
        var result = TextReader.Read(text, culture);
        if (claudeError is not null)
            result = result with { Notes = [claudeError + " The built-in reader was used instead.", .. result.Notes ?? []] };
        return result.IsEmpty ? (null, "Nothing could be read from this document. Enter the details by hand.") : (result, null);
    }

    private const string Instructions = """
        Read this document for an Australian bookkeeping system and fill in the fields. It is a receipt, a supplier's bill
        (tax invoice), a statement, or a share trade contract note. Use null for anything not shown; never guess.
        Dates are yyyy-MM-dd. Amounts are plain numbers with no currency symbol. total includes any tax; tax is the GST,
        VAT or sales tax shown. currency is the three-letter code only if it isn't Australian dollars. For a contract note
        give the security code, side (Buy or Sell), units, price per unit and brokerage. description is a short summary
        of what was bought, in at most ten words.
        """;

    private async Task<ReadResult?> ReadWithClaudeAsync(byte[] data, string contentType, CancellationToken ct)
    {
        AnthropicClient client = new() { ApiKey = options.AnthropicApiKey };
        var b64 = Convert.ToBase64String(data);
        List<ContentBlockParam> content = DocumentText.IsPdf(contentType)
            ? [new DocumentBlockParam { Source = new Base64PdfSource { Data = b64 } }, new TextBlockParam { Text = Instructions }]
            : [new ImageBlockParam { Source = new Base64ImageSource { Data = b64, MediaType = ImageMediaType(contentType) } }, new TextBlockParam { Text = Instructions }];

        var response = await client.Messages.Create(new MessageCreateParams
        {
            Model = options.Model,
            MaxTokens = 4000,
            OutputConfig = new OutputConfig { Effort = Effort.Low, Format = new JsonOutputFormat { Schema = Schema } },
            Messages = [new() { Role = Role.User, Content = content }],
        }, ct);
        if (response.StopReason == "refusal")
            return null;
        var json = string.Concat(response.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text));
        return string.IsNullOrWhiteSpace(json) ? null : FromJson(json);
    }

    private static MediaType ImageMediaType(string contentType) => contentType switch
    {
        "image/png" => MediaType.ImagePng,
        "image/gif" => MediaType.ImageGif,
        "image/webp" => MediaType.ImageWebP,
        _ => MediaType.ImageJpeg,
    };

    private static readonly Dictionary<string, JsonElement> Schema = BuildSchema();

    private static Dictionary<string, JsonElement> BuildSchema()
    {
        object Nullable(string type) => new { type = new[] { type, "null" } };
        var properties = new Dictionary<string, object>
        {
            ["kind"] = new { type = "string", @enum = ReadKinds.All },
            ["payee"] = Nullable("string"),
            ["date"] = Nullable("string"),
            ["dueDate"] = Nullable("string"),
            ["reference"] = Nullable("string"),
            ["abn"] = Nullable("string"),
            ["description"] = Nullable("string"),
            ["total"] = Nullable("number"),
            ["tax"] = Nullable("number"),
            ["currency"] = Nullable("string"),
            ["tradeCode"] = Nullable("string"),
            ["tradeSide"] = Nullable("string"),
            ["units"] = Nullable("number"),
            ["price"] = Nullable("number"),
            ["brokerage"] = Nullable("number"),
        };
        return new Dictionary<string, JsonElement>
        {
            ["type"] = JsonSerializer.SerializeToElement("object"),
            ["properties"] = JsonSerializer.SerializeToElement(properties),
            ["required"] = JsonSerializer.SerializeToElement(properties.Keys),
            ["additionalProperties"] = JsonSerializer.SerializeToElement(false),
        };
    }

    /// <summary>Claude's answer as a result. Bad values become blanks rather than errors.</summary>
    public static ReadResult? FromJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            string? S(string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
                ? v.GetString()!.Trim() : null;
            decimal? N(string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d) ? d : null;
            DateOnly? D(string name) => S(name) is { } s && DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                ? d : null;
            var kind = S("kind") is { } k && ReadKinds.All.Contains(k) ? k : ReadKinds.Receipt;
            var currency = S("currency")?.ToUpperInvariant();
            if (currency is not null && currency.Length != 3)
                currency = null;
            var side = S("tradeSide") is { } sd ? sd.StartsWith("S", StringComparison.OrdinalIgnoreCase) ? "Sell" : "Buy" : null;
            return new ReadResult(kind, S("payee"), D("date"), S("reference"), S("abn")?.Replace(" ", ""), S("description"), N("total"), N("tax"),
                currency, D("dueDate"), S("tradeCode")?.ToUpperInvariant(), side, N("units"), N("price"), N("brokerage"), "Claude");
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
