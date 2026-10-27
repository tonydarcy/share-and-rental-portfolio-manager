using System.Globalization;
using PortfolioManager.Core.Validation;

namespace PortfolioManager.Core.Import;

public enum ImportRowStatus { Ready, Duplicate, Unsupported, Error }

/// <param name="PortfolioCurrency">The portfolio's own currency (tblUsers.strPortfolioCurrency), usually AUD.</param>
/// <param name="RatesAreForeignPerPortfolioUnit">
/// Navexa's exchange rate converts the security's currency into the portfolio currency (1 USD = 1.54 AUD).
/// Set this when the file's rates already run the other way (1 AUD = 0.65 USD), which is how this site stores them.
/// </param>
public sealed record NavexaOptions(string PortfolioCurrency, bool RatesAreForeignPerPortfolioUnit = false, DateTime? Today = null);

public abstract class NavexaRow
{
    public int Line { get; init; }
    public DateTime Date { get; set; }
    public string Exchange { get; set; } = "";
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Currency { get; set; } = "";
    public string Comment { get; set; } = "";
    public List<string> Errors { get; } = [];
    public List<string> Warnings { get; } = [];
    /// <summary>Why the row can't be imported by this page (e.g. a share split), when it is a valid Navexa row.</summary>
    public string? Unsupported { get; set; }
    /// <summary>Set when the portfolio already has the same trade or payout.</summary>
    public string? DuplicateOf { get; set; }

    public ImportRowStatus Status =>
        Errors.Count > 0 ? ImportRowStatus.Error
        : Unsupported is not null ? ImportRowStatus.Unsupported
        : DuplicateOf is not null ? ImportRowStatus.Duplicate
        : ImportRowStatus.Ready;
}

public sealed class NavexaTrade : NavexaRow
{
    public string RawType { get; set; } = "";
    /// <summary>"Buy" or "Sell", as the trade procs expect, or "Reward" for coins received as crypto income.</summary>
    public string Type { get; set; } = "";
    public decimal Quantity { get; set; }
    public decimal Price { get; set; }
    public decimal Brokerage { get; set; }
    /// <summary>Units of the trade currency per 1 unit of portfolio currency (1 when they are the same), as tblShareTrades stores it.</summary>
    public decimal ExchangeRate { get; set; } = 1m;

    public decimal OrderTotal => Type == "Sell" ? Price * Quantity - Brokerage : Price * Quantity + Brokerage;
    public decimal PortfolioTotal => ExchangeRate == 0 ? 0 : Math.Round(OrderTotal / ExchangeRate, 2);
}

/// <summary>A dividend or distribution, with every amount already in the portfolio currency.</summary>
public sealed class NavexaPayout : NavexaRow
{
    public decimal Franked { get; set; }
    public decimal Unfranked { get; set; }
    public decimal FrankingCredit { get; set; }
    public decimal ForeignTax { get; set; }
    public decimal TfnWithheld { get; set; }
    /// <summary>Cash received: what tblPayouts.dblTotal holds.</summary>
    public decimal Net { get; set; }
    public decimal Gross => Franked + Unfranked;
}

public sealed record NavexaParseResult<T>(IReadOnlyList<T> Rows, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings)
    where T : NavexaRow
{
    public int Count(ImportRowStatus status) => Rows.Count(r => r.Status == status);
    public IEnumerable<T> Ready => Rows.Where(r => r.Status == ImportRowStatus.Ready);
}

public sealed record ColumnInfo(string Field, bool Required, string[] Headers, string Notes);

/// <summary>
/// Reads Navexa's trade and income CSV exports. Columns are found by header name (any order,
/// any case, spaces and punctuation ignored), so a few likely variants of each name are accepted.
/// </summary>
public static class NavexaParser
{
    public const int MaxRows = 10_000;
    private const int HeaderSearchRows = 10;

    public static readonly IReadOnlyList<ColumnInfo> TradeColumns =
    [
        new("Date", true, ["Date", "Trade Date", "Transaction Date", "Effective Date"], "Day/month/year or year-month-day."),
        new("Code", true, ["Symbol", "Code", "Ticker", "Stock Code", "Security Code", "ASX Code"], "CBA.AX and ASX:CBA are also understood."),
        new("Exchange", false, ["Exchange", "Market", "Exchange Code", "Stock Exchange"], "Navexa codes like AXW or NAS become ASX or NASDAQ. Missing: ASX."),
        new("Type", true, ["Type", "Trade Type", "Transaction Type", "Action"], "Buy and Sell are imported (Opening Balance as a Buy); others are listed but not imported."),
        new("Quantity", true, ["Quantity", "Units", "Qty", "Shares", "Number of Units"], "Part units are fine (up to 8 decimal places). A negative sell quantity is fine."),
        new("Price", true, ["Price", "Unit Price", "Price per Unit", "Trade Price"], "In the trade currency."),
        new("Brokerage", false, ["Brokerage", "Fees", "Fee", "Commission", "Brokerage Fee"], "In the trade currency. Missing: 0."),
        new("BrokerageCurrency", false, ["Brokerage Currency", "Fee Currency", "Brokerage Currency Code"], "Only needed when brokerage was paid in another currency."),
        new("Currency", false, ["Currency", "Trade Currency", "Currency Code", "Price Currency"], "Missing: the exchange's usual currency."),
        new("ExchangeRate", false, ["Exchange Rate", "FX Rate", "Exch Rate", "FX"], "Needed for trades not in the portfolio currency."),
        new("Name", false, ["Name", "Security Name", "Company Name", "Holding Name", "Stock Name", "Security"], ""),
        new("Notes", false, ["Notes", "Note", "Comment", "Comments", "Memo"], "Added to the trade comment."),
    ];

    public static readonly IReadOnlyList<ColumnInfo> IncomeColumns =
    [
        new("Date", true, ["Payment Date", "Pay Date", "Paid Date", "Date Paid", "Date"], "The payment date (not the ex date)."),
        new("Code", true, ["Symbol", "Code", "Ticker", "Stock Code", "Security Code", "ASX Code"], ""),
        new("Exchange", false, ["Exchange", "Market", "Exchange Code", "Stock Exchange"], "As for trades. Missing: ASX."),
        new("Gross", false, ["Gross Amount", "Gross", "Gross Income", "Amount", "Total", "Total Amount", "Dividend", "Income", "Total Income"], "Franked + unfranked, before tax withheld."),
        new("Net", false, ["Net Amount", "Net", "Net Income", "Cash Amount", "Net Payment", "Amount Received"], "Cash received. Missing: gross less tax withheld."),
        new("Franked", false, ["Franked", "Franked Amount"], ""),
        new("Unfranked", false, ["Unfranked", "Unfranked Amount"], "If franked and unfranked are both missing, the gross is treated as unfranked."),
        new("FrankingCredits", false, ["Franking Credits", "Franking Credit", "Imputation Credits", "Franking Credit Amount"], ""),
        new("WithholdingTax", false, ["Withholding Tax", "Foreign Tax Withheld", "Foreign Tax", "Withholding", "Tax Withheld", "Foreign Withholding Tax", "Foreign Income Tax"], "Recorded as foreign tax paid."),
        new("TfnWithholding", false, ["TFN Withholding", "TFN Withheld", "TFN Withholding Tax", "TFN Amount Withheld"], "Recorded as TFN withholding."),
        new("Currency", false, ["Currency", "Currency Code", "Dividend Currency", "Payment Currency"], "Missing: the exchange's usual currency."),
        new("ExchangeRate", false, ["Exchange Rate", "FX Rate", "Exch Rate", "FX"], "Needed for income not in the portfolio currency."),
        new("Type", false, ["Type", "Income Type", "Dividend Type", "Category"], "Dividends, distributions and interest are imported."),
        new("Reinvested", false, ["Reinvested", "DRP", "Is Reinvested", "Dividend Reinvestment"], ""),
        new("Name", false, ["Name", "Security Name", "Company Name", "Holding Name", "Stock Name", "Security"], ""),
        new("Notes", false, ["Notes", "Note", "Comment", "Comments", "Memo"], ""),
    ];

    private static readonly string[] AmountFields = ["Gross", "Net", "Franked", "Unfranked"];

    public static NavexaParseResult<NavexaTrade> ParseTrades(string csv, NavexaOptions options)
    {
        var rows = new List<NavexaTrade>();
        var warnings = new List<string>();
        if (!TryFindHeader(csv, TradeColumns, null, out var cols, out var data, out var error))
            return new([], [error], []);
        if (!cols.Has("Exchange"))
            warnings.Add("The file has no exchange column, so every trade is treated as ASX unless its code says otherwise (e.g. ASX:CBA).");

        var today = (options.Today ?? DateTime.Today).Date;
        foreach (var (line, fields) in data)
        {
            var row = new NavexaTrade { Line = line };
            rows.Add(row);
            var get = cols.Reader(fields);

            ReadCommon(row, get, today, options.PortfolioCurrency);

            row.RawType = get("Type");
            var type = Normalise(row.RawType);
            switch (type)
            {
                case "stakingreward" or "staking" or "cryptoairdrop" or "airdrop" or "cryptominingreward" or "miningreward" when Crypto.CryptoCodes.IsCrypto(row.Exchange):
                    row.Type = "Reward";
                    row.Warnings.Add("Crypto reward: its value is income and the cost base of the coins received.");
                    break;
                case "buy" or "purchase" or "b":
                    row.Type = "Buy";
                    break;
                case "sell" or "sale" or "s" or "disposal":
                    row.Type = "Sell";
                    break;
                case "openingbalance":
                    row.Type = "Buy";
                    row.Warnings.Add("Opening balance imported as a Buy on this date at this price.");
                    break;
                case "":
                    row.Errors.Add("No trade type.");
                    break;
                default:
                    row.Unsupported = UnsupportedTradeMessage(type, row.RawType);
                    break;
            }

            var qtyError = Number(get("Quantity"), out var qty);
            qty = Math.Abs(qty);
            // Navexa keeps coins to more places than this site's 8
            if (qtyError is null && Crypto.CryptoCodes.IsCrypto(row.Exchange) && Math.Round(qty, FormRules.UnitDecimals) is var rounded && rounded != qty && rounded > 0)
            {
                row.Warnings.Add($"{qty:0.##########} units rounded to {rounded:0.########} (this site keeps 8 decimal places).");
                qty = rounded;
            }
            if (row.Unsupported is not null)
                row.Quantity = qtyError is null && FormRules.UnitsProblem(qty) is null ? qty : 0;
            else if (qtyError is not null)
                row.Errors.Add($"Quantity {qtyError}");
            else if (qty == 0)
                row.Errors.Add("Quantity is zero.");
            else if (FormRules.UnitsProblem(qty) is { } problem)
                row.Errors.Add(problem);
            else
                row.Quantity = qty;

            var priceError = Number(get("Price"), out var price);
            if (row.Unsupported is not null)
                row.Price = priceError is null ? price : 0;
            else if (priceError is not null)
                row.Errors.Add($"Price {priceError}");
            else if (price < 0)
                row.Errors.Add("Price is negative.");
            else
                row.Price = price;

            var brokerageText = get("Brokerage");
            if (!IsBlank(brokerageText))
            {
                var brokerageError = Number(brokerageText, out var brokerage);
                if (brokerageError is not null)
                    row.Errors.Add($"Brokerage {brokerageError}");
                else
                    row.Brokerage = Math.Abs(brokerage);
            }

            var rate = ReadRate(row, get, options, out var portfolioPerForeign);
            row.ExchangeRate = rate;

            // Navexa lets brokerage be paid in another currency, typically the portfolio's own
            var brokerageCurrency = get("BrokerageCurrency").Trim().ToUpperInvariant();
            if (row.Brokerage != 0 && brokerageCurrency.Length > 0 && row.Currency.Length > 0
                && !brokerageCurrency.Equals(row.Currency, StringComparison.OrdinalIgnoreCase))
            {
                if (brokerageCurrency.Equals(options.PortfolioCurrency, StringComparison.OrdinalIgnoreCase) && portfolioPerForeign > 0)
                {
                    row.Brokerage = Math.Round(row.Brokerage / portfolioPerForeign, 4);
                    row.Warnings.Add($"Brokerage was in {brokerageCurrency}; converted to {row.Currency} {row.Brokerage:0.00}.");
                }
                else
                    row.Errors.Add($"Brokerage is in {brokerageCurrency}, which can't be converted to {row.Currency}.");
            }

            if (row.Type == "Sell" && row.Brokerage > row.Price * row.Quantity && row.Errors.Count == 0)
                row.Warnings.Add("Brokerage is more than the sale proceeds.");
            if (row.Type == "Reward" && row.Errors.Count == 0 && (row.Price <= 0 || row.ExchangeRate <= 0 || row.Price * row.Quantity / row.ExchangeRate < 0.005m))
                row.Errors.Add("A crypto reward needs the coins' price on the day, to give its value.");

            var notes = get("Notes").Trim();
            row.Comment = Truncate(notes.Length > 0 ? $"Imported from Navexa: {notes}" : "Imported from Navexa", 500);

            // A row that won't be imported anyway is shown as unsupported, not as an error
            if (row.Unsupported is not null)
                row.Errors.Clear();
        }

        MarkRepeatsWithinFile(rows, TradeKey);
        return new(rows, [], warnings);
    }

    public static NavexaParseResult<NavexaPayout> ParseIncome(string csv, NavexaOptions options)
    {
        var rows = new List<NavexaPayout>();
        var warnings = new List<string>();
        if (!TryFindHeader(csv, IncomeColumns, AmountFields, out var cols, out var data, out var error))
            return new([], [error], []);
        if (!cols.Has("Exchange"))
            warnings.Add("The file has no exchange column, so all income is treated as ASX unless its code says otherwise (e.g. ASX:CBA).");

        var today = (options.Today ?? DateTime.Today).Date;
        foreach (var (line, fields) in data)
        {
            var row = new NavexaPayout { Line = line };
            rows.Add(row);
            var get = cols.Reader(fields);

            ReadCommon(row, get, today, options.PortfolioCurrency);

            var type = get("Type").Trim();
            if (Crypto.CryptoCodes.IsCrypto(row.Exchange))
                row.Unsupported = "Crypto income needs the coins received, which the income file doesn't give. Use the trades file's staking rows, " +
                    "the Navexa sync, or Income > Add a crypto reward.";
            if (type.Length > 0 && !new[] { "dividend", "distribution", "interest", "income", "coupon", "payout" }
                    .Any(t => Normalise(type).Contains(t)))
                row.Unsupported = $"Income type \"{type}\" isn't imported. Add it by hand if it belongs in this portfolio.";

            decimal? Amount(string field)
            {
                var text = get(field);
                if (IsBlank(text))
                    return null;
                var err = Number(text, out var value);
                if (err is null)
                    return value;
                row.Errors.Add($"{field} {err}");
                return null;
            }

            var gross = Amount("Gross");
            var net = Amount("Net");
            var franked = Amount("Franked");
            var unfranked = Amount("Unfranked");
            var credits = Amount("FrankingCredits") ?? 0m;
            var foreignTax = Math.Abs(Amount("WithholdingTax") ?? 0m);
            var tfn = Math.Abs(Amount("TfnWithholding") ?? 0m);

            if (franked is null && unfranked is null)
            {
                unfranked = gross ?? (net is { } n ? n + foreignTax + tfn : null);
                franked = 0m;
            }
            franked ??= 0m;
            unfranked ??= 0m;
            var splitGross = franked.Value + unfranked.Value;
            if (gross is { } g && Math.Abs(g - splitGross) > 0.01m)
                row.Warnings.Add($"Franked + unfranked ({splitGross:0.00}) doesn't match the gross amount ({g:0.00}); the franked/unfranked split is used.");

            var expectedNet = splitGross - foreignTax - tfn;
            if (net is { } given && Math.Abs(given - expectedNet) > 0.01m)
                row.Warnings.Add($"Net amount ({given:0.00}) isn't gross less tax withheld ({expectedNet:0.00}); the net amount is used as the cash received.");
            var cash = net ?? expectedNet;

            ReadRate(row, get, options, out var portfolioPerForeign);
            decimal ToPortfolio(decimal amount) => Math.Round(amount * portfolioPerForeign, 2);
            row.Franked = ToPortfolio(franked.Value);
            row.Unfranked = ToPortfolio(unfranked.Value);
            row.FrankingCredit = ToPortfolio(credits);
            row.ForeignTax = ToPortfolio(foreignTax);
            row.TfnWithheld = ToPortfolio(tfn);
            row.Net = ToPortfolio(cash);

            if (row.Errors.Count == 0 && row.Unsupported is null)
            {
                if (row.Gross < 0 || row.Net < 0 || row.FrankingCredit < 0)
                    row.Errors.Add("Negative amounts (reversals) aren't supported.");
                else if (row.Gross == 0 && row.Net == 0)
                    row.Errors.Add("No amount.");
            }

            if (IsYes(get("Reinvested")))
                row.Warnings.Add("Reinvested (DRP): the dividend is imported, but the new units are not. Add them as a Buy if you want them.");

            var notes = get("Notes").Trim();
            row.Comment = Truncate(notes.Length > 0 ? $"Imported from Navexa: {notes}" : "Imported from Navexa", 500);

            // A row that won't be imported anyway is shown as unsupported, not as an error
            if (row.Unsupported is not null)
                row.Errors.Clear();
        }

        MarkRepeatsWithinFile(rows, PayoutKey);
        return new(rows, [], warnings);
    }

    private static string UnsupportedTradeMessage(string type, string raw) => type switch
    {
        "dividendreinvestment" or "drp" or "reinvestment" =>
            "Dividend reinvestment isn't imported. The dividend comes in from the income file; add the new units as a Buy if you want them.",
        "split" or "sharesplit" or "consolidation" =>
            $"{raw} isn't imported: record it under Transactions, Corporate actions, after the trades are in.",
        "bonus" or "bonusissue" =>
            $"{raw} isn't imported: this site has no way to record it yet. Adjust the holding by hand.",
        "returnofcapital" or "capitalreturn" =>
            "Return of capital isn't imported: record it under Transactions, Corporate actions, after the trades are in.",
        "transferin" or "transferout" or "deposit" or "withdrawal" =>
            $"{raw} isn't imported: transfers carry no price. Enter it as a Buy or Sell if it changes the holding.",
        "mergecancel" or "mergebuy" or "merge" or "takeover" or "demerger" =>
            $"{raw} isn't imported: record corporate actions by hand.",
        _ => $"Trade type \"{raw}\" isn't imported.",
    };

    private static void ReadCommon(NavexaRow row, Func<string, string> get, DateTime today, string portfolioCurrency)
    {
        var dateText = get("Date");
        if (IsBlank(dateText))
            row.Errors.Add("No date.");
        else if (!TryDate(dateText, out var date))
            row.Errors.Add($"Date \"{dateText.Trim()}\" isn't a date.");
        else if (date > today)
            row.Errors.Add($"Date {date:d} is in the future.");
        else
            row.Date = date;

        var (symbolExchange, code) = NavexaExchanges.SplitSymbol(get("Code"));
        var exchangeText = get("Exchange").Trim();
        if (exchangeText.Length == 0)
            exchangeText = symbolExchange ?? "ASX";
        var exchange = NavexaExchanges.Map(exchangeText);
        row.Exchange = exchange.Code;
        row.Code = code.ToUpperInvariant();
        row.Name = Truncate(get("Name").Trim(), 500);

        if (row.Code.Length == 0)
            row.Errors.Add("No code.");
        else if (row.Code.Length > 50)
            row.Errors.Add("Code is longer than 50 characters.");
        if (row.Exchange.Length > 50)
            row.Errors.Add("Exchange is longer than 50 characters.");
        else if (!exchange.Recognised)
            row.Warnings.Add($"Exchange \"{exchangeText}\" isn't one this import knows; it will be stored as \"{row.Exchange}\".");

        var currency = get("Currency").Trim().ToUpperInvariant();
        if (currency.Length == 0)
            currency = exchange.Currency ?? (Crypto.CryptoCodes.IsCrypto(row.Exchange) ? portfolioCurrency.ToUpperInvariant() : "");
        // A coin is recorded under the price provider's code: BTC traded in AUD is BTC-AUD
        if (Crypto.CryptoCodes.IsCrypto(row.Exchange) && row.Code.Length > 0 && currency.Length > 0)
            row.Code = Crypto.CryptoCodes.SiteCode(row.Code, currency);
        if (currency.Length == 0)
            row.Errors.Add("No currency, and the exchange doesn't say which to use.");
        else if (currency.Length > 3 || !currency.All(char.IsAsciiLetterUpper))
            row.Errors.Add($"Currency \"{currency}\" isn't a 3-letter code.");
        row.Currency = currency;
    }

    /// <summary>
    /// Returns the rate as this site stores it (trade currency per portfolio unit) and, via
    /// portfolioPerForeign, the multiplier that turns a trade-currency amount into portfolio currency.
    /// </summary>
    private static decimal ReadRate(NavexaRow row, Func<string, string> get, NavexaOptions options, out decimal portfolioPerForeign)
    {
        portfolioPerForeign = 1m;
        if (row.Currency.Length == 0 || row.Currency.Equals(options.PortfolioCurrency, StringComparison.OrdinalIgnoreCase))
            return 1m;

        var text = get("ExchangeRate");
        if (IsBlank(text))
        {
            row.Errors.Add($"No exchange rate for a {row.Currency} amount.");
            return 1m;
        }
        var err = Number(text, out var rate);
        if (err is not null)
        {
            row.Errors.Add($"Exchange rate {err}");
            return 1m;
        }
        if (rate <= 0)
        {
            row.Errors.Add("Exchange rate must be more than zero.");
            return 1m;
        }

        if (options.RatesAreForeignPerPortfolioUnit)
        {
            portfolioPerForeign = 1m / rate;
            return rate;
        }
        portfolioPerForeign = rate;
        return Math.Round(1m / rate, 10);
    }

    internal static string TradeKey(NavexaTrade t) =>
        $"{t.Exchange}|{t.Code}|{t.Type}|{t.Date:yyyyMMdd}|{t.Quantity:0.########}|{t.Price:0.0000}".ToUpperInvariant();

    internal static string PayoutKey(NavexaPayout p) =>
        $"{p.Exchange}|{p.Code}|{p.Date:yyyyMMdd}|{p.Net:0.00}".ToUpperInvariant();

    private static void MarkRepeatsWithinFile<T>(List<T> rows, Func<T, string> key) where T : NavexaRow
    {
        var seen = new Dictionary<string, int>();
        foreach (var row in rows.Where(r => r.Status == ImportRowStatus.Ready))
        {
            var k = key(row);
            if (seen.TryGetValue(k, out var first))
                row.Warnings.Add($"Same as line {first} of this file. Both will be imported; check it isn't listed twice.");
            else
                seen[k] = row.Line;
        }
    }

    // ---- Header handling ----

    private sealed class ColumnMap(Dictionary<string, int> map)
    {
        public bool Has(string field) => map.ContainsKey(field);

        public Func<string, string> Reader(string[] fields) => field =>
            map.TryGetValue(field, out var i) && i < fields.Length ? fields[i] : "";
    }

    private static bool TryFindHeader(string csv, IReadOnlyList<ColumnInfo> columns, string[]? needOneOf,
        out ColumnMap map, out IReadOnlyList<(int Line, string[] Fields)> data, out string error)
    {
        map = new([]);
        data = [];
        var rows = Csv.Parse(csv);
        if (rows.Count == 0)
        {
            error = "The file is empty.";
            return false;
        }

        // Some exports put a title or date range above the header, so look a little way down
        for (var h = 0; h < Math.Min(HeaderSearchRows, rows.Count); h++)
        {
            var found = MatchHeader(rows[h].Fields, columns);
            if (columns.Where(c => c.Required).All(c => found.ContainsKey(c.Field))
                && (needOneOf is null || needOneOf.Any(found.ContainsKey)))
            {
                data = rows.Skip(h + 1).ToList();
                map = new(found);
                if (data.Count > MaxRows)
                {
                    error = $"The file has {data.Count:N0} rows; the most this page takes is {MaxRows:N0}. Split it by year.";
                    return false;
                }
                error = "";
                return true;
            }
        }

        var required = columns.Where(c => c.Required).Select(c => string.Join(" / ", c.Headers.Take(2)));
        error = "Couldn't find the header row. It needs columns for " + string.Join(", ", required)
            + (needOneOf is null ? "" : ", and at least one amount (" + string.Join(", ", needOneOf) + ")") + ".";
        return false;
    }

    private static Dictionary<string, int> MatchHeader(string[] header, IReadOnlyList<ColumnInfo> columns)
    {
        var normalised = header.Select(Normalise).ToArray();
        var found = new Dictionary<string, int>();
        var taken = new HashSet<int>();
        foreach (var column in columns)
        {
            // Aliases are in order of preference, e.g. "Payment Date" beats a plain "Date"
            foreach (var alias in column.Headers.Select(Normalise))
            {
                var i = Array.FindIndex(normalised, n => n == alias);
                if (i >= 0 && !taken.Contains(i))
                {
                    found[column.Field] = i;
                    taken.Add(i);
                    break;
                }
            }
        }
        return found;
    }

    // ---- Value parsing ----

    internal static string Normalise(string s) =>
        new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static bool IsBlank(string s) => s.Trim() is "" or "-" or "—" or "–" or "N/A" or "n/a";

    private static bool IsYes(string s) => s.Trim().ToLowerInvariant() is "yes" or "y" or "true" or "1";

    /// <summary>Parses amounts like "1,234.50", "$12.00", "A$ 3", "(4.10)", "USD 5". Returns an error message, or null when it parsed.</summary>
    public static string? Number(string text, out decimal value)
    {
        value = 0;
        var s = text.Trim();
        if (IsBlank(s))
            return "is missing.";

        var negative = s.StartsWith('(') && s.EndsWith(')');
        if (negative)
            s = s[1..^1];
        // Drop currency codes and symbols, thousands separators and spaces
        var cleaned = new string(s.Replace('\u2212', '-').Where(c => char.IsDigit(c) || c is '.' or '-' or '+').ToArray());
        if (cleaned.Length == 0 || !decimal.TryParse(cleaned, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out value))
            return $"\"{text.Trim()}\" isn't a number.";
        if (negative)
            value = -value;
        return null;
    }

    private static readonly string[] DateFormats =
    [
        "yyyy-MM-dd", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ssZ", "yyyy-MM-ddTHH:mm:ss.fffZ", "yyyy-MM-dd HH:mm:ss",
        "d/M/yyyy", "d/M/yy", "d/M/yyyy H:mm", "d/M/yyyy H:mm:ss", "d/M/yyyy h:mm:ss tt",
        "d-M-yyyy", "d.M.yyyy", "d MMM yyyy", "d-MMM-yyyy", "d-MMM-yy", "d MMMM yyyy", "MMM d, yyyy", "yyyy/MM/dd",
    ];

    /// <summary>Australian day/month order, as Navexa uses for Australian accounts. ISO dates are fine too.</summary>
    public static bool TryDate(string text, out DateTime date)
    {
        // Invariant, not en-AU: on Windows (ICU) en-AU abbreviates months as "Oct.", so "15 Oct 2025" would fail.
        // The formats are explicit and day-first, so nothing here depends on the culture's date order.
        var ok = DateTime.TryParseExact(text.Trim(), DateFormats, CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces, out date);
        date = date.Date;
        return ok;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
