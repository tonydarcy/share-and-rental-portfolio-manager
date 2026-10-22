using System.Globalization;
using System.Text.RegularExpressions;

namespace PortfolioManager.Core.Imports;

/// <summary>A broker's trade export that the Import share trades page can read.</summary>
public sealed record BrokerFormat(string Id, string Name, string HowToExport);

/// <summary>
/// Trade exports from Australian and overseas brokers, turned into the same rows as the standard upload
/// (<see cref="ShareTradeCsv"/>), so they get the same preview, duplicate check and import.
/// Most brokers export a CSV with a header row, read by matching column names (<see cref="ParseWithHeaders"/>);
/// CommSec's account transactions and Interactive Brokers' activity statement have their own layouts.
/// Lines that aren't buys or sells (dividends, deposits, fees) are left out.
/// </summary>
public static partial class BrokerCsv
{
    public const string Auto = "auto";
    public const string Standard = "standard";

    public static readonly IReadOnlyList<BrokerFormat> Formats =
    [
        new(Auto, "Work it out from the file", "Picks one of the formats below from the file's header."),
        new(Standard, "Standard upload file", "Exchange, Code, Name, Buy/Sell, Date, Quantity, Price, Brokerage, Currency, Exchange rate, Comment, no header."),
        new("commsec", "CommSec", "Portfolio > Accounts > Transactions, download as CSV (lines like \"B 100 CBA @ 95.50\"), or the trade confirmations CSV."),
        new("cmc", "CMC Invest", "Portfolio > Trade history (or Confirmations), export to CSV."),
        new("selfwealth", "SelfWealth", "Trade History, export to CSV."),
        new("stake", "Stake", "Activity (ASX or Wall St), export trades to CSV."),
        new("pearler", "Pearler", "Portfolio > Transactions, export to CSV."),
        new("superhero", "Superhero", "Account > Transaction history (trades), export to CSV."),
        new("ibkr", "Interactive Brokers", "Activity statement (or a Flex query of trades) as CSV."),
        new("headers", "Another broker (CSV with a header row)", "Columns named like Date, Buy/Sell (or Type), Code (or Symbol), Quantity (or Units), Price, Brokerage."),
    ];

    public static BrokerFormat? Find(string? id) =>
        Formats.FirstOrDefault(f => f.Id.Equals(id ?? Auto, StringComparison.OrdinalIgnoreCase));

    /// <summary>The format a file looks like, for the "work it out" choice.</summary>
    public static string Detect(string text)
    {
        var first = CsvImport.Lines(text).Take(30).ToList();
        if (first.Any(l => l.Text.StartsWith("Trades,Header", StringComparison.OrdinalIgnoreCase)))
            return "ibkr";
        foreach (var (_, line) in first)
        {
            var cols = CsvImport.Split(line).Select(Clean).ToList();
            if (cols.Contains("details") && cols.Any(c => c.StartsWith("debit")))
                return "commsec";
            if (FindColumns(cols) is { } map && map.Has(Role.Code) && map.Has(Role.Quantity))
                return "headers";
        }
        return Standard;
    }

    /// <param name="defaultMarket">The exchange for rows that don't say (most Australian brokers' files don't).</param>
    public static ImportPreview<ShareTradeRow> Parse(string format, string text, DateTime today, string defaultCurrency = "AUD", string defaultMarket = "ASX")
    {
        var id = (format ?? Auto).Trim().ToLowerInvariant();
        if (id == Auto)
            id = Detect(text);
        defaultMarket = string.IsNullOrWhiteSpace(defaultMarket) ? "ASX" : defaultMarket.Trim().ToUpperInvariant();
        var source = Find(id)?.Name ?? "broker file";
        return id switch
        {
            Standard => ShareTradeCsv.Parse(text, today, defaultCurrency),
            "ibkr" => ParseInteractiveBrokers(text, today, defaultCurrency, defaultMarket),
            "commsec" when Detect(text) == "commsec" => ParseCommSecTransactions(text, today, defaultCurrency, defaultMarket),
            _ => ParseWithHeaders(text, today, defaultCurrency, defaultMarket, source),
        };
    }

    private enum Role { Date, Type, Code, Market, Name, Quantity, Price, Fee, FeeInclusive, Gst, Currency, Rate, Gross, Net, Reference }

    // Column names, lower case with punctuation and spaces squeezed (see Clean)
    private static readonly (Role Role, string[] Names)[] Aliases =
    [
        (Role.Date, ["trade date", "date", "transaction date", "date time", "trade date time", "tradedate", "execution date", "order date", "confirmation date"]),
        (Role.Type, ["buy sell", "type", "side", "action", "transaction type", "trade type", "direction", "b s", "buy or sell"]),
        (Role.Code, ["code", "symbol", "ticker", "security code", "asx code", "stock code", "instrument code", "security", "stock", "share code"]),
        (Role.Market, ["market", "exchange", "listing exchange", "market code"]),
        (Role.Name, ["name", "security name", "company", "company name", "description", "instrument", "stock name", "security description"]),
        (Role.Quantity, ["quantity", "units", "qty", "shares", "volume", "no of units", "number of units", "unit quantity", "filled quantity"]),
        (Role.Price, ["price", "average price", "avg price", "trade price", "unit price", "price per unit", "effective price", "executed price",
                      "t price", "share price", "average price usd", "avg price usd", "effective price usd", "price aud", "price usd", "execution price"]),
        (Role.FeeInclusive, ["brokerage inc gst", "brokerage incl gst", "brokerage including gst", "fees inc gst", "total brokerage", "total fees"]),
        (Role.Fee, ["brokerage", "fees", "fee", "commission", "comm fee", "brokerage aud", "brokerage usd", "fees usd", "fees aud", "commission usd"]),
        (Role.Gst, ["gst", "gst on brokerage"]),
        (Role.Currency, ["currency", "trade currency", "ccy"]),
        (Role.Rate, ["fx rate", "exchange rate", "fx"]),
        (Role.Gross, ["consideration", "gross amount", "gross value", "trade value", "value", "gross consideration", "value usd", "value aud"]),
        (Role.Net, ["total", "net amount", "net value", "total value", "amount", "net consideration", "total amount", "total value usd", "total aud", "settlement amount"]),
        (Role.Reference, ["reference", "confirmation number", "contract note", "contract note number", "order id", "trade id", "ref", "confirmation no", "order number"]),
    ];

    private sealed class ColumnMap
    {
        public Dictionary<Role, List<int>> Columns { get; } = [];
        public bool Has(Role r) => Columns.ContainsKey(r);
        public int? First(Role r) => Columns.TryGetValue(r, out var l) ? l[0] : null;
    }

    private static ColumnMap? FindColumns(IReadOnlyList<string> cleaned)
    {
        var map = new ColumnMap();
        for (var i = 0; i < cleaned.Count; i++)
        {
            foreach (var (role, names) in Aliases)
            {
                if (!names.Contains(cleaned[i]))
                    continue;
                // The first column of each role wins, except fees, which add up
                if (!map.Columns.TryGetValue(role, out var list))
                    map.Columns[role] = list = [];
                if (list.Count == 0 || role == Role.Fee)
                    list.Add(i);
                break;
            }
        }
        return map.Has(Role.Date) || map.Has(Role.Code) ? map : null;
    }

    /// <summary>
    /// A CSV with a header row: the header is the first line that names a code and a quantity column,
    /// anything above it (a title, account details) is skipped. Fees add up (brokerage + GST + other fees,
    /// unless a column says it includes GST). A missing price or brokerage is worked out from the totals.
    /// </summary>
    public static ImportPreview<ShareTradeRow> ParseWithHeaders(string text, DateTime today, string defaultCurrency, string defaultMarket, string source = "broker file")
    {
        var all = CsvImport.Lines(text).ToList();
        ColumnMap? map = null;
        var start = 0;
        for (var i = 0; i < Math.Min(all.Count, 30) && map is null; i++)
        {
            var candidate = FindColumns(CsvImport.Split(all[i].Text).Select(Clean).ToList());
            if (candidate is not null && candidate.Has(Role.Code) && candidate.Has(Role.Quantity) && candidate.Has(Role.Date))
            {
                map = candidate;
                start = i + 1;
            }
        }
        if (map is null)
            return ImportPreview<ShareTradeRow>.Failed(
                "No header row was found. The file needs columns for the date, the code (or symbol) and the quantity (or units). " +
                "If it's the standard upload file, pick that format.");

        var lines = new List<ParsedLine<ShareTradeRow>>();
        foreach (var (number, line) in all.Skip(start))
        {
            if (lines.Count >= CsvImport.MaxLines)
                return ImportPreview<ShareTradeRow>.Failed($"The file has more than {CsvImport.MaxLines:N0} lines; split it into smaller files.");
            var f = CsvImport.Split(line);
            string Get(Role r) => map.First(r) is { } i && i < f.Count ? f[i] : "";

            // Totals and blank rows at the end, and lines that aren't trades, are left out
            var codeText = Get(Role.Code);
            if (codeText.Length == 0 || codeText.StartsWith("total", StringComparison.OrdinalIgnoreCase))
                continue;
            var quantityText = Get(Role.Quantity);
            var hasQuantity = TryAmount(quantityText, out var signedQuantity);
            var type = TradeType(Get(Role.Type));
            if (type is null && map.Has(Role.Type) && Get(Role.Type).Length > 0)
                continue; // e.g. Dividend, Deposit
            if (type is null && hasQuantity && signedQuantity != 0)
                type = signedQuantity < 0 ? "Sell" : "Buy";

            var fees = 0m;
            var feesOk = true;
            var inclusive = map.First(Role.FeeInclusive) is { } inc && inc < f.Count ? f[inc] : null;
            if (!string.IsNullOrEmpty(inclusive))
                feesOk = TryAmount(inclusive, out fees);
            else
            {
                foreach (var i in map.Columns.GetValueOrDefault(Role.Fee, []).Concat(map.Columns.GetValueOrDefault(Role.Gst, [])))
                    if (i < f.Count && f[i].Length > 0)
                    {
                        if (TryAmount(f[i], out var fee)) fees += Math.Abs(fee);
                        else feesOk = false;
                    }
            }
            fees = Math.Abs(fees);
            var hasFees = !string.IsNullOrEmpty(inclusive) || map.Columns.GetValueOrDefault(Role.Fee, []).Concat(map.Columns.GetValueOrDefault(Role.Gst, [])).Any(i => i < f.Count && f[i].Length > 0);

            var raw = new RawTrade(
                Date: Get(Role.Date), Type: type, Code: codeText, Market: Get(Role.Market), Name: Get(Role.Name),
                Quantity: hasQuantity ? Math.Abs(signedQuantity) : null, QuantityText: quantityText,
                Price: TryAmount(Get(Role.Price), out var p) ? Math.Abs(p) : null,
                Fees: feesOk ? (hasFees ? fees : null) : -1,
                Gross: TryAmount(Get(Role.Gross), out var g) ? Math.Abs(g) : null,
                Net: TryAmount(Get(Role.Net), out var n) ? Math.Abs(n) : null,
                Currency: Get(Role.Currency), Rate: Get(Role.Rate), Reference: Get(Role.Reference));
            lines.Add(Build(number, line, raw, today, defaultCurrency, defaultMarket, source));
        }
        return lines.Count == 0 ? ImportPreview<ShareTradeRow>.Failed("The file has no buys or sells in it.") : new(lines);
    }

    /// <summary>
    /// CommSec account transactions: Date, Reference, Details, Debit($), Credit($), Balance($), where a trade's details read
    /// "B 1,000 CBA @ 95.500000" or "S 500 BHP @ 45.20". The debit (buy) or credit (sell) includes brokerage, so brokerage is
    /// the difference from units times price.
    /// </summary>
    public static ImportPreview<ShareTradeRow> ParseCommSecTransactions(string text, DateTime today, string defaultCurrency, string defaultMarket)
    {
        var all = CsvImport.Lines(text).ToList();
        int date = -1, reference = -1, details = -1, debit = -1, credit = -1, start = -1;
        for (var i = 0; i < Math.Min(all.Count, 30) && start < 0; i++)
        {
            var cols = CsvImport.Split(all[i].Text).Select(Clean).ToList();
            if (!cols.Contains("details"))
                continue;
            date = cols.IndexOf("date");
            reference = cols.IndexOf("reference");
            details = cols.IndexOf("details");
            debit = cols.FindIndex(c => c.StartsWith("debit"));
            credit = cols.FindIndex(c => c.StartsWith("credit"));
            start = i + 1;
        }
        if (start < 0 || date < 0 || details < 0)
            return ImportPreview<ShareTradeRow>.Failed("This doesn't look like a CommSec transactions file (no Date and Details columns).");

        var lines = new List<ParsedLine<ShareTradeRow>>();
        foreach (var (number, line) in all.Skip(start))
        {
            var f = CsvImport.Split(line);
            string At(int i) => i >= 0 && i < f.Count ? f[i] : "";
            var m = CommSecDetails().Match(At(details));
            if (!m.Success)
                continue; // transfers, interest, fees
            var buy = m.Groups["side"].Value.Equals("B", StringComparison.OrdinalIgnoreCase);
            TryAmount(m.Groups["qty"].Value, out var qty);
            TryAmount(m.Groups["price"].Value, out var price);
            var amount = TryAmount(At(buy ? debit : credit), out var a) ? Math.Abs(a) : (decimal?)null;
            var raw = new RawTrade(At(date), buy ? "Buy" : "Sell", m.Groups["code"].Value, "", "", qty, m.Groups["qty"].Value, price,
                Fees: null, Gross: null, Net: amount, Currency: "", Rate: "", Reference: At(reference));
            lines.Add(Build(number, line, raw, today, defaultCurrency, defaultMarket, "CommSec"));
        }
        return lines.Count == 0 ? ImportPreview<ShareTradeRow>.Failed("The file has no buys or sells in it.") : new(lines);
    }

    /// <summary>
    /// Interactive Brokers' activity statement: the "Trades" section's Header row names the columns, and each
    /// "Trades,Data,Order" row is a trade (stocks only). Quantity is negative for a sale; Comm/Fee is negative.
    /// A Flex query CSV with a plain header row is read by <see cref="ParseWithHeaders"/> instead.
    /// </summary>
    public static ImportPreview<ShareTradeRow> ParseInteractiveBrokers(string text, DateTime today, string defaultCurrency, string defaultMarket)
    {
        List<string>? header = null;
        var lines = new List<ParsedLine<ShareTradeRow>>();
        foreach (var (number, line) in CsvImport.Lines(text))
        {
            var f = CsvImport.Split(line);
            if (f.Count < 3 || !f[0].Equals("Trades", StringComparison.OrdinalIgnoreCase))
                continue;
            if (f[1].Equals("Header", StringComparison.OrdinalIgnoreCase))
            {
                header = f.Select(Clean).ToList();
                continue;
            }
            if (header is null || !f[1].Equals("Data", StringComparison.OrdinalIgnoreCase))
                continue;
            string Col(params string[] names)
            {
                foreach (var n in names)
                    if (header.IndexOf(n) is >= 0 and var i && i < f.Count)
                        return f[i];
                return "";
            }
            var discriminator = Col("datadiscriminator");
            if (discriminator.Length > 0 && !discriminator.Equals("Order", StringComparison.OrdinalIgnoreCase))
                continue;
            var category = Col("asset category");
            if (category.Length > 0 && !category.StartsWith("Stock", StringComparison.OrdinalIgnoreCase))
                continue; // options, forex, futures

            var hasQty = TryAmount(Col("quantity"), out var q);
            var currency = Col("currency").ToUpperInvariant();
            var market = Col("exchange", "listing exchange");
            if (market.Length == 0 || market.Equals("IDEALPRO", StringComparison.OrdinalIgnoreCase))
                market = MarketForCurrency(currency) ?? defaultMarket;
            var raw = new RawTrade(Col("date time", "trade date", "tradedate"), hasQty ? (q < 0 ? "Sell" : "Buy") : null,
                Col("symbol"), market, Col("description"), hasQty ? Math.Abs(q) : null, Col("quantity"),
                TryAmount(Col("t price", "tradeprice", "trade price"), out var p) ? Math.Abs(p) : null,
                TryAmount(Col("comm fee", "ibcommission", "commission"), out var fee) ? Math.Abs(fee) : 0,
                Gross: TryAmount(Col("proceeds"), out var g) ? Math.Abs(g) : null, Net: null,
                Currency: currency, Rate: Col("fxratetobase", "fx rate to base"), Reference: Col("tradeid", "trade id", "ib order id"));
            lines.Add(Build(number, line, raw, today, defaultCurrency, defaultMarket, "Interactive Brokers"));
        }
        if (header is null)
            return ImportPreview<ShareTradeRow>.Failed("No Trades section was found. Export the activity statement as CSV with the Trades section included.");
        return lines.Count == 0 ? ImportPreview<ShareTradeRow>.Failed("The Trades section has no share buys or sells.") : new(lines);
    }

    /// <summary>What one line of any broker file says, before checking.</summary>
    /// <param name="Fees">Null when the file has no fee column; -1 when a fee couldn't be read.</param>
    private sealed record RawTrade(
        string Date, string? Type, string Code, string Market, string Name, decimal? Quantity, string QuantityText, decimal? Price,
        decimal? Fees, decimal? Gross, decimal? Net, string Currency, string Rate, string Reference);

    private static ParsedLine<ShareTradeRow> Build(int number, string line, RawTrade t, DateTime today, string defaultCurrency, string defaultMarket, string source)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        var (code, market) = SplitCode(t.Code, t.Market, defaultMarket);
        if (code.Length is 0 or > 50)
            errors.Add("The code is missing or too long.");
        if (market.Length is 0 or > 50)
            errors.Add("The exchange is too long.");
        if (t.Type is null)
            errors.Add("Couldn't tell whether this is a buy or a sell.");

        DateTime? date = ReadDate(t.Date);
        if (date is null)
            errors.Add($"'{t.Date}' isn't a date this page can read (use e.g. 2024-02-29 or 29/02/2024).");
        else if (date > today.Date)
            errors.Add("The trade date is in the future.");

        int quantity = 0;
        if (t.Quantity is not { } q || q <= 0)
            errors.Add($"'{t.QuantityText}' isn't a number of units above zero.");
        else if (q != decimal.Truncate(q) || q > int.MaxValue)
            errors.Add("Part units (fractional shares) can't be imported yet; enter this trade by hand rounded to whole units.");
        else
            quantity = (int)q;

        // Fill in a missing price or brokerage from the totals: a buy's net = gross + fees, a sale's net = gross - fees
        var buy = t.Type == "Buy";
        var price = t.Price;
        var fees = t.Fees;
        if (fees == -1)
        {
            errors.Add("The brokerage or fees couldn't be read.");
            fees = 0;
        }
        if (price is null && quantity > 0)
        {
            if (t.Gross is { } gross)
                price = gross / quantity;
            else if (t.Net is { } net)
                price = (buy ? net - (fees ?? 0) : net + (fees ?? 0)) / quantity;
        }
        if (price is null)
            errors.Add("There's no price (or total to work it out from).");
        if (fees is null && price is { } pr && quantity > 0)
        {
            var gross = t.Gross ?? Math.Round(pr * quantity, 2, MidpointRounding.AwayFromZero);
            fees = t.Net is { } net ? Math.Max(0, Math.Round(buy ? net - gross : gross - net, 2)) : 0;
        }

        var currency = t.Currency.Trim().ToUpperInvariant() is { Length: 3 } c && c.All(char.IsAsciiLetter) ? c : defaultCurrency;
        decimal rate = 1;
        if (t.Rate.Length > 0 && (!TryAmount(t.Rate, out rate) || rate <= 0))
        {
            errors.Add("The exchange rate must be a number above zero.");
            rate = 1;
        }
        else if (t.Rate.Length == 0 && !currency.Equals(defaultCurrency, StringComparison.OrdinalIgnoreCase))
            warnings.Add($"Traded in {currency} with no exchange rate in the file, so the rate is 1. Fix it on the trade after importing if your portfolio needs it.");

        var comment = $"Imported from {source}" + (t.Reference.Length > 0 ? $" ({t.Reference})" : "");
        if (comment.Length > 500) comment = comment[..500];
        var name = t.Name.Length > 500 ? t.Name[..500] : t.Name;

        var row = errors.Count == 0
            ? new ShareTradeRow(market, code, name, t.Type!, date!.Value, quantity, Math.Round(price!.Value, 6), Math.Round(fees ?? 0, 2), currency, rate, comment)
            : null;
        return new ParsedLine<ShareTradeRow>(number, line, row, errors, warnings);
    }

    /// <summary>"CBA.AX", "CBA.ASX" or "ASX:CBA" carry the exchange with them; otherwise the market column, then the default.</summary>
    private static (string Code, string Market) SplitCode(string code, string market, string defaultMarket)
    {
        code = code.Trim().ToUpperInvariant();
        market = market.Trim().ToUpperInvariant();
        if (code.Split(':') is [var ex, var c] && ex.Length > 0 && c.Length > 0)
            return (c, ex);
        foreach (var (suffix, exchange) in new[] { (".AX", "ASX"), (".ASX", "ASX"), (".AU", "ASX"), (".NZ", "NZX"), (".L", "LSE") })
            if (code.EndsWith(suffix) && code.Length > suffix.Length)
                return (code[..^suffix.Length], exchange);
        return (code, market.Length > 0 ? NormaliseMarket(market) : defaultMarket);
    }

    /// <summary>The site's code for an exchange name or code (XASX, NMS, LON ...), as the Navexa import maps them.</summary>
    private static string NormaliseMarket(string market) => Import.NavexaExchanges.Map(market).Code;

    private static string? MarketForCurrency(string currency) => currency switch
    {
        "AUD" => "ASX",
        "NZD" => "NZX",
        "GBP" => "LSE",
        "CAD" => "TSX",
        "HKD" => "HKEX",
        "SGD" => "SGX",
        _ => null,
    };

    private static string? TradeType(string s) => s.Trim().ToLowerInvariant() switch
    {
        "buy" or "b" or "bought" or "purchase" or "buy to open" or "market buy" or "limit buy" => "Buy",
        "sell" or "s" or "sold" or "sale" or "sell to close" or "market sell" or "limit sell" => "Sell",
        var x when x.StartsWith("buy") => "Buy",
        var x when x.StartsWith("sell") => "Sell",
        _ => null,
    };

    private static readonly string[] DateFormats =
    [
        "yyyy-MM-dd", "d/MM/yyyy", "d/M/yyyy", "dd/MM/yyyy", "d-MM-yyyy", "dd-MM-yyyy", "d-MMM-yyyy", "dd-MMM-yyyy",
        "d MMM yyyy", "dd MMM yyyy", "yyyyMMdd", "d/MM/yy", "dd/MM/yy", "yyyy/MM/dd", "d.MM.yyyy",
    ];

    /// <summary>Day-first dates (as Australian brokers write them), with any time after the date dropped.</summary>
    private static DateTime? ReadDate(string text)
    {
        var s = text.Trim();
        var cut = s.IndexOfAny([' ', 'T', ',', ';']);
        // "29 Feb 2024" has spaces of its own
        if (DateTime.TryParseExact(s, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var whole))
            return whole.Date;
        if (cut > 0 && DateTime.TryParseExact(s[..cut], DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return d.Date;
        var parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 3 && DateTime.TryParseExact(string.Join(' ', parts[..3]), DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d3))
            return d3.Date;
        return null;
    }

    /// <summary>A number such as 1,234.50, $95.50, A$12, -3 or (12.00).</summary>
    public static bool TryAmount(string? text, out decimal value)
    {
        var s = (text ?? "").Trim().Replace("A$", "").Replace("US$", "").Replace("$", "").Replace(" ", "");
        var negative = s.StartsWith('(') && s.EndsWith(')');
        if (negative)
            s = s[1..^1];
        foreach (var code in new[] { "AUD", "USD", "NZD", "GBP" })
            s = s.Replace(code, "", StringComparison.OrdinalIgnoreCase);
        if (!decimal.TryParse(s, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out value))
            return false;
        if (negative)
            value = -value;
        return true;
    }

    /// <summary>A header name, lower case, with punctuation as spaces and spaces squeezed: "Avg. Price (USD)" is "avg price usd".</summary>
    private static string Clean(string header) =>
        Squeeze().Replace(NonWord().Replace(header.ToLowerInvariant().Replace("$", ""), " "), " ").Trim();

    [GeneratedRegex(@"^(?<side>[BS])\s+(?<qty>[\d,]+(?:\.\d+)?)\s+(?<code>[A-Z0-9.]{1,12})\s+@\s+\$?(?<price>[\d,]*\.?\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex CommSecDetails();

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NonWord();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Squeeze();
}
