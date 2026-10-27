using System.Globalization;
using System.Text.RegularExpressions;
using PortfolioManager.Core.Imports;
using PortfolioManager.Core.Trades;
using PortfolioManager.Core.Validation;

namespace PortfolioManager.Core.Crypto;

/// <summary>A crypto exchange's export that the Import crypto page can read.</summary>
public sealed record CryptoExchangeFormat(string Id, string Name, string HowToExport);

/// <summary>One line of an exchange file, as the trade, swap or reward it records.</summary>
public sealed record CryptoImportRow(int Line, DateTime Date, string Description, NewTrade? Trade = null, NewSwap? Swap = null, NewReward? Reward = null)
{
    /// <summary>Rewards and buys before swaps and sells on the same day, so a same-day sale has units to sell.</summary>
    public int PostingOrder => Reward is not null ? 0 : Trade?.Type == TradeType.Buy ? 1 : Swap is not null ? 2 : 3;

    /// <summary>Keys (see <see cref="ShareTradeRow.TradeKey"/>) of the trades this line adds, for spotting ones already on file.</summary>
    public IEnumerable<string> TradeKeys => (Trade is { } t ? [t] : Swap is { } s ? CryptoService.SwapTrades(s) : [])
        .Select(t => ShareTradeRow.TradeKey(t.Market, t.Code, t.TypeText, t.Date.ToDateTime(TimeOnly.MinValue), t.Quantity, t.Price));
}

/// <summary>A line of the file and what happens to it: imported (Row), left out (Skipped, with the reason) or an error.</summary>
public sealed record CryptoLine(int Line, string Text, CryptoImportRow? Row, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings, string? Skipped = null)
{
    public bool IsValid => Row is not null && Errors.Count == 0;
}

public sealed record CryptoPreview(IReadOnlyList<CryptoLine> Lines, string Source, string? FileError = null)
{
    public int ValidCount => Lines.Count(l => l.IsValid);
    public int ErrorCount => Lines.Count(l => l.Skipped is null && !l.IsValid);
    public int SkippedCount => Lines.Count(l => l.Skipped is not null);
    public IEnumerable<CryptoImportRow> ValidRows => Lines.Where(l => l.IsValid).Select(l => l.Row!);
    public static CryptoPreview Failed(string error) => new([], "", error);
}

/// <summary>
/// Trade history exports from crypto exchanges, read by their column names. Buys and sells against the portfolio's
/// currency become trades on market CRYPTO (BTC bought for AUD is BTC-AUD); a coin traded for another coin (or a
/// Coinbase convert) is a swap at the AUD value in the file; staking, interest, airdrop and learning rewards are
/// income paid in coins. Deposits, withdrawals and transfers between wallets aren't sales, so they are left out.
/// The column names come from each exchange's published layout and need checking against real files.
/// </summary>
public static partial class CryptoExchangeCsv
{
    public const string Auto = "auto";

    public static readonly IReadOnlyList<CryptoExchangeFormat> Formats =
    [
        new(Auto, "Work it out from the file", "Picks one of the exchanges below from the file's header."),
        new("coinbase", "Coinbase", "Profile > Statements (or Reports) > Transaction history, as CSV, in Australian dollars."),
        new("swyftx", "Swyftx", "Profile > Transaction history (or Tax reports > Transaction report), export as CSV."),
        new("coinspot", "CoinSpot", "Profile > Order history (Buy/Sell orders), export as CSV."),
        new("independentreserve", "Independent Reserve", "Account > Trade history (Closed orders), export as CSV."),
        new("headers", "Another exchange (CSV with a header row)", "Columns named like Date, Type, Asset (or a pair such as BTC/AUD), Quantity, Price, Total and Fee."),
    ];

    public static CryptoExchangeFormat? Find(string? id) =>
        Formats.FirstOrDefault(f => f.Id.Equals(id ?? Auto, StringComparison.OrdinalIgnoreCase));

    private enum Role { Date, Type, Asset, Pair, Base, Quote, Quantity, Price, PriceCurrency, Subtotal, Total, Fee, FeeAud, FeeCurrency, Notes, Received, ReceivedAsset }

    // Header names, cleaned (lower case, punctuation squeezed to single spaces). The first match of each role wins.
    private static readonly (Role Role, string[] Names)[] Aliases =
    [
        (Role.Date, ["timestamp", "date", "transaction date", "date created", "date time", "time", "created at", "date completed", "datetime"]),
        (Role.Type, ["transaction type", "type", "order type", "event", "side", "action", "trade type", "category"]),
        (Role.Asset, ["asset", "coin", "currency", "symbol", "asset code", "code", "primary asset", "cryptocurrency"]),
        (Role.Pair, ["market", "pair", "trading pair", "instrument", "product"]),
        (Role.Base, ["primary currency", "base currency", "base asset", "primary currency code", "currency code primary"]),
        (Role.Quote, ["secondary currency", "quote currency", "quote asset", "secondary currency code", "currency code secondary"]),
        (Role.Quantity, ["quantity transacted", "amount", "quantity", "volume filled", "volume", "units", "filled", "size", "qty", "amount filled"]),
        (Role.Price, ["rate ex fee", "price at transaction", "spot price at transaction", "average price", "avg price", "price", "rate",
                      "price aud", "rate aud", "unit price", "price per coin", "rate inc fee", "trade price"]),
        (Role.PriceCurrency, ["price currency", "spot price currency"]),
        (Role.Subtotal, ["subtotal", "value", "gross", "gross amount", "trade value", "value aud", "total value"]),
        (Role.Total, ["total aud", "total inclusive of fees and or spread", "total", "total inc gst", "amount aud", "aud value", "total amount", "net amount"]),
        (Role.FeeAud, ["fee aud inc gst", "fee aud", "fees aud"]),
        (Role.Fee, ["fees and or spread", "fee", "fees", "fee amount", "commission"]),
        (Role.FeeCurrency, ["fee currency", "fee currency code", "fee asset"]),
        (Role.Notes, ["notes", "note", "description", "memo", "comment"]),
        (Role.Received, ["amount received", "received amount", "to amount"]),
        (Role.ReceivedAsset, ["received asset", "received currency", "to asset", "to currency"]),
    ];

    private static readonly HashSet<string> Fiat = ["AUD", "USD", "NZD", "GBP", "EUR", "CAD", "SGD", "HKD", "JPY"];

    private sealed class ColumnMap(Dictionary<Role, int> map, IReadOnlyList<string> headers)
    {
        public bool Has(Role r) => map.ContainsKey(r);
        public string Get(IReadOnlyList<string> f, Role r) => map.TryGetValue(r, out var i) && i < f.Count ? f[i].Trim() : "";
        /// <summary>The column's cleaned header name, e.g. "total aud".</summary>
        public string Header(Role r) => map.TryGetValue(r, out var i) ? headers[i] : "";
    }

    private static ColumnMap? FindColumns(IReadOnlyList<string> header)
    {
        var map = new Dictionary<Role, int>();
        var cleaned = header.Select(Clean).ToList();
        // Exact names first, in alias order, so "price" doesn't take "price currency"
        foreach (var (role, names) in Aliases)
            foreach (var name in names)
            {
                var i = cleaned.FindIndex(c => c == name);
                if (i >= 0 && !map.ContainsKey(role) && !map.ContainsValue(i))
                {
                    map[role] = i;
                    break;
                }
            }
        return map.ContainsKey(Role.Date) && map.ContainsKey(Role.Quantity) && (map.ContainsKey(Role.Asset) || map.ContainsKey(Role.Pair) || map.ContainsKey(Role.Base))
            ? new ColumnMap(map, cleaned) : null;
    }

    /// <summary>The exchange a file looks like, from its header.</summary>
    public static string Detect(IReadOnlyList<string> header)
    {
        var h = header.Select(Clean).ToHashSet();
        if (h.Contains("quantity transacted") || h.Contains("total inclusive of fees and or spread"))
            return "coinbase";
        if (h.Contains("rate ex fee") || h.Contains("fee aud inc gst") || h.Contains("total aud") && h.Contains("market"))
            return "coinspot";
        if (h.Contains("primary currency") || h.Contains("volume filled") || h.Contains("order guid"))
            return "independentreserve";
        if (h.Any(c => c.Contains("swyftx")) || h.Contains("event") && h.Contains("asset"))
            return "swyftx";
        return "headers";
    }

    public static CryptoPreview Parse(string format, string text, DateTime today, string portfolioCurrency = "AUD")
    {
        var currency = portfolioCurrency.Trim().ToUpperInvariant();
        var all = CsvImport.Lines(text).ToList();
        ColumnMap? map = null;
        IReadOnlyList<string> header = [];
        var start = 0;
        for (var i = 0; i < Math.Min(all.Count, 30) && map is null; i++)
        {
            var fields = CsvImport.Split(all[i].Text);
            if (FindColumns(fields) is { } found)
            {
                map = found;
                header = fields;
                start = i + 1;
            }
        }
        if (map is null)
            return CryptoPreview.Failed("No header row was found. The file needs columns for the date, the coin (or a pair such as BTC/AUD) and the amount.");

        var id = (format ?? Auto).Trim().ToLowerInvariant();
        if (id == Auto || Find(id) is null)
            id = Detect(header);
        var source = Find(id)!.Name;

        var lines = new List<CryptoLine>();
        foreach (var (number, line) in all.Skip(start))
        {
            if (lines.Count >= CsvImport.MaxLines)
                return CryptoPreview.Failed($"The file has more than {CsvImport.MaxLines:N0} lines; split it into smaller files.");
            var f = CsvImport.Split(line);
            if (f.All(string.IsNullOrWhiteSpace) || map.Get(f, Role.Date).StartsWith("total", StringComparison.OrdinalIgnoreCase))
                continue;
            lines.Add(ReadLine(number, line, f, map, today, currency, source));
        }
        return lines.Count == 0 ? CryptoPreview.Failed("The file has no transactions in it.") with { Source = source } : new(lines, source);
    }

    private enum Kind { Buy, Sell, Swap, Reward, Transfer, Unknown }

    private static Kind KindOf(string type)
    {
        var t = Clean(type);
        if (t.Length == 0)
            return Kind.Unknown;
        if (t.Contains("convert") || t.Contains("swap") || t == "trade" || t == "exchange")
            return Kind.Swap;
        if (t.Contains("staking") || t.Contains("stake reward") || t.Contains("reward") || t.Contains("interest") || t.Contains("airdrop")
            || t.Contains("mining") || t.Contains("earn") || t.Contains("inflation") || t.Contains("income") || t.Contains("learning") || t.Contains("referral"))
            return Kind.Reward;
        if (t.Contains("deposit") || t.Contains("withdraw") || t.Contains("send") || t.Contains("receive") || t.Contains("transfer"))
            return Kind.Transfer;
        if (t.Contains("buy") || t.Contains("bid") || t.Contains("purchase") || t == "b")
            return Kind.Buy;
        if (t.Contains("sell") || t.Contains("offer") || t.Contains("ask") || t == "s" || t == "sale")
            return Kind.Sell;
        return Kind.Unknown;
    }

    private static string RewardKind(string type)
    {
        var t = Clean(type);
        return t.Contains("airdrop") ? RewardKinds.Airdrop
            : t.Contains("mining") ? RewardKinds.Mining
            : t.Contains("staking") || t.Contains("stake") || t.Contains("inflation") ? RewardKinds.Staking
            : RewardKinds.Other;
    }

    private static CryptoLine ReadLine(int number, string text, IReadOnlyList<string> f, ColumnMap map, DateTime today, string currency, string source)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        string Get(Role r) => map.Get(f, r);
        CryptoLine Skip(string why) => new(number, text, null, [], [], why);

        var typeText = Get(Role.Type);
        var kind = KindOf(typeText);
        if (kind == Kind.Transfer)
            return Skip($"{typeText}: a transfer in or out isn't a sale or purchase, so it isn't imported.");
        if (kind == Kind.Unknown && map.Has(Role.Type))
            return Skip(typeText.Length == 0 ? "No transaction type." : $"\"{typeText}\" lines aren't imported.");

        // The coin and what it was priced in: a pair (BTC/AUD), primary and secondary columns, or the coin and the price currency
        string coin, quote;
        if (map.Has(Role.Pair) && SplitPair(Get(Role.Pair)) is { } pair)
            (coin, quote) = pair;
        else if (map.Has(Role.Base))
            (coin, quote) = (Get(Role.Base), map.Has(Role.Quote) ? Get(Role.Quote) : currency);
        else
            (coin, quote) = (Get(Role.Asset), Get(Role.PriceCurrency));
        coin = CoinName(coin);
        quote = CoinName(quote.Length > 0 ? quote : currency);
        if (coin.Length == 0)
            return new(number, text, null, ["There's no coin on this line."], []);
        if (kind == Kind.Unknown)
            kind = Signed(Get(Role.Quantity)) < 0 ? Kind.Sell : Kind.Buy;
        if (Fiat.Contains(coin))
            return Skip($"{coin} {typeText} is a cash movement, not a coin, so it isn't imported.");

        DateTime? date = ReadDate(Get(Role.Date));
        if (date is null)
            errors.Add($"'{Get(Role.Date)}' isn't a date this page can read.");
        else if (date > today.Date)
            errors.Add("The date is in the future.");

        var quantity = Math.Abs(Signed(Get(Role.Quantity)) ?? 0);
        if (quantity == 0)
            errors.Add($"'{Get(Role.Quantity)}' isn't an amount of coins above zero.");
        else
        {
            var rounded = Math.Round(quantity, FormRules.UnitDecimals);
            if (rounded != quantity)
            {
                warnings.Add($"{quantity:0.##########} units rounded to {rounded:0.########} (this site keeps 8 decimal places).");
                quantity = rounded;
            }
            if (FormRules.UnitsProblem(quantity) is { } problem)
                errors.Add(problem);
        }

        var price = Amount(Get(Role.Price));
        var subtotal = Amount(Get(Role.Subtotal));
        var total = Amount(Get(Role.Total));
        var fee = Amount(Get(Role.FeeAud)) ?? Amount(Get(Role.Fee)) ?? 0;
        var feeCurrency = CoinName(Get(Role.FeeCurrency));
        if (!map.Has(Role.FeeAud) && feeCurrency.Length > 0 && feeCurrency != quote && fee != 0)
        {
            warnings.Add($"The fee was paid in {feeCurrency}, so it isn't included. Add it to the cost by hand if it matters.");
            fee = 0;
        }
        var note = Get(Role.Notes);
        var comment = Truncate($"Imported from {source}" + (note.Length > 0 ? $": {note}" : ""), 500);
        var day = date is { } d0 ? DateOnly.FromDateTime(d0) : default;

        // Coinbase's convert says what came back only in its notes: "Converted 0.01 BTC to 0.15 ETH"
        if (kind == Kind.Swap || (kind is Kind.Buy or Kind.Sell && !Fiat.Contains(quote)))
        {
            string fromCoin, toCoin;
            decimal fromUnits, toUnits;
            if (ConvertNote().Match(note) is { Success: true } m)
            {
                (fromCoin, toCoin) = (CoinName(m.Groups["from"].Value), CoinName(m.Groups["to"].Value));
                fromUnits = Amount(m.Groups["fromQty"].Value) ?? 0;
                toUnits = Amount(m.Groups["toQty"].Value) ?? 0;
            }
            else if (map.Has(Role.Received) && Amount(Get(Role.Received)) is { } received)
            {
                (fromCoin, fromUnits) = (coin, quantity);
                (toCoin, toUnits) = (CoinName(Get(Role.ReceivedAsset)), Math.Abs(received));
            }
            else if (kind is Kind.Buy or Kind.Sell && price is > 0)
            {
                // A coin-for-coin order on a pair like ETH/BTC: the amount is in the first coin, the price in the second
                var other = Math.Round(Math.Abs(price.Value * quantity), FormRules.UnitDecimals);
                (fromCoin, fromUnits, toCoin, toUnits) = kind == Kind.Buy ? (quote, other, coin, quantity) : (coin, quantity, quote, other);
            }
            else
            {
                errors.Add("A swap needs the coin and units on both sides. Record it on Transactions > Record a crypto swap.");
                return new(number, text, null, errors, warnings);
            }
            fromUnits = Math.Round(Math.Abs(fromUnits), FormRules.UnitDecimals);
            toUnits = Math.Round(Math.Abs(toUnits), FormRules.UnitDecimals);

            // The value in the portfolio currency: a column named for it (Total AUD), or Coinbase's subtotal when its prices are in it
            var named = currency.ToLowerInvariant();
            decimal? value = map.Header(Role.Total).Contains(named) ? total
                : map.Header(Role.Subtotal).Contains(named) ? subtotal
                : quote == currency ? subtotal ?? total
                : null;
            if (value is not > 0)
                errors.Add($"There's no {currency} value for this swap in the file. Record it on Transactions > Record a crypto swap with its value on the day.");
            if (fromUnits <= 0 || toUnits <= 0 || toCoin.Length == 0)
                errors.Add("The units on one side of the swap are missing.");
            if (errors.Count > 0)
                return new(number, text, null, errors, warnings);
            var swap = new NewSwap(day, CryptoCodes.SiteCode(fromCoin, currency), fromUnits, CryptoCodes.SiteCode(toCoin, currency), toUnits,
                Math.Round(Math.Abs(value!.Value), 2), currency, comment);
            return new(number, text, new CryptoImportRow(number, date!.Value,
                $"Swap {fromUnits:0.########} {fromCoin} for {toUnits:0.########} {toCoin} ({swap.Value:N2} {currency})", Swap: swap), errors, warnings);
        }

        if (kind == Kind.Reward)
        {
            var named = currency.ToLowerInvariant();
            var value = map.Header(Role.Total).Contains(named) ? total
                : map.Header(Role.Subtotal).Contains(named) ? subtotal
                : quote == currency ? subtotal ?? total ?? (price is { } p ? Math.Round(p * quantity, 2) : null)
                : null;
            if (value is not { } v || Math.Round(Math.Abs(v), 2) < 0.01m)
                errors.Add($"There's no {currency} value for this reward. Add it on Income > Add a crypto reward with its value on the day.");
            if (errors.Count > 0)
                return new(number, text, null, errors, warnings);
            var rewardKind = RewardKind(typeText);
            var reward = new NewReward(day, CryptoCodes.SiteCode(coin, currency), quantity, Math.Round(Math.Abs(value!.Value), 2), rewardKind,
                CryptoEntry.RewardComment(rewardKind, $"imported from {source}"));
            return new(number, text, new CryptoImportRow(number, date!.Value,
                $"{RewardKinds.Label(rewardKind)}: {quantity:0.########} {coin} ({reward.Value:N2} {currency})", Reward: reward), errors, warnings);
        }

        // A buy or sell against cash
        if (price is null && quantity > 0)
            price = subtotal is { } s ? Math.Abs(s) / quantity
                : total is { } t ? (kind == Kind.Buy ? Math.Abs(t) - fee : Math.Abs(t) + fee) / quantity
                : null;
        if (price is not > 0)
            errors.Add("There's no price (or total to work it out from).");
        fee = Math.Round(Math.Abs(fee), 2);
        if (kind == Kind.Sell && price is > 0 && fee >= price.Value * quantity)
        {
            warnings.Add($"The fee ({fee:0.00}) is more than the sale proceeds; it is cut to the proceeds, so the sale nets nil.");
            fee = Math.Floor(price.Value * quantity * 100) / 100;
        }
        if (quote != currency)
            warnings.Add($"Traded in {quote}: the exchange rate is filled in from the FX close on file, or 1 if there isn't one.");
        if (errors.Count > 0)
            return new(number, text, null, errors, warnings);

        var type = kind == Kind.Buy ? TradeType.Buy : TradeType.Sell;
        var trade = new NewTrade(day, type, CryptoCodes.Market, CryptoCodes.SiteCode(coin, quote), null, quantity, Math.Round(price!.Value, 10),
            fee, quote, 1, comment, null);
        if (TradeEntry.OrderTotal(type, quantity, trade.Price, fee) > FormRules.MaxMoney)
            return new(number, text, null, ["That trade is too large to record."], warnings);
        return new(number, text, new CryptoImportRow(number, date!.Value,
            $"{trade.TypeText} {quantity:0.########} {coin} at {trade.Price:0.########} {quote}", Trade: trade), errors, warnings);
    }

    /// <summary>Warnings on lines whose trades look like ones already in the portfolio, or earlier in the file.</summary>
    public static CryptoPreview FlagDuplicates(CryptoPreview preview, IReadOnlySet<string> existingKeys)
    {
        var inFile = new HashSet<string>();
        var lines = preview.Lines.Select(l =>
        {
            if (!l.IsValid || !l.Row!.TradeKeys.Any())
                return l;
            var keys = l.Row.TradeKeys.ToList();
            var warnings = new List<string>(l.Warnings);
            if (keys.Any(existingKeys.Contains))
                warnings.Add("A trade with the same coin, type, date, units and price is already in this portfolio.");
            if (!keys.All(inFile.Add))
                warnings.Add("The same trade appears earlier in the file.");
            return l with { Warnings = warnings };
        }).ToList();
        return preview with { Lines = lines };
    }

    // ---------- Helpers ----------

    private static (string Coin, string Quote)? SplitPair(string pair)
    {
        var parts = pair.Trim().Split(['/', '-', '_', ':'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 2 ? (parts[0], parts[1]) : null;
    }

    /// <summary>Upper case, with Independent Reserve's XBT as BTC.</summary>
    private static string CoinName(string s)
    {
        var c = s.Trim().ToUpperInvariant();
        return c == "XBT" ? "BTC" : c;
    }

    private static decimal? Amount(string text) =>
        !string.IsNullOrWhiteSpace(text) && BrokerCsv.TryAmount(text, out var v) ? v : null;

    private static decimal? Signed(string text) => Amount(text);

    private static readonly string[] DateFormats =
    [
        "yyyy-MM-dd", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ssZ", "yyyy-MM-ddTHH:mm:ss.fffZ", "yyyy-MM-dd HH:mm:ss 'UTC'",
        "d/MM/yyyy", "d/M/yyyy", "dd/MM/yyyy", "d/MM/yyyy h:mm tt", "dd/MM/yyyy h:mm tt", "d/MM/yyyy H:mm", "dd/MM/yyyy HH:mm", "dd/MM/yyyy HH:mm:ss",
        "d/MM/yyyy h:mm:ss tt", "dd/MM/yyyy h:mm:ss tt", "d MMM yyyy", "dd MMM yyyy", "d-MMM-yyyy", "dd-MMM-yyyy", "yyyy/MM/dd",
    ];

    /// <summary>Year-first or day-first dates (as Australian exchanges write them), with any time dropped.</summary>
    private static DateTime? ReadDate(string text)
    {
        var s = text.Trim();
        if (s.Length == 0)
            return null;
        if (DateTime.TryParseExact(s, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var whole))
            return whole.Date;
        var cut = s.IndexOfAny([' ', 'T']);
        if (cut > 0 && DateTime.TryParseExact(s[..cut], DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return d.Date;
        return null;
    }

    private static string Truncate(string s, int max) => s.Length > max ? s[..max] : s;

    private static string Clean(string header) =>
        Squeeze().Replace(NonWord().Replace(header.ToLowerInvariant(), " "), " ").Trim();

    [GeneratedRegex(@"Converted\s+(?<fromQty>[\d.,]+)\s+(?<from>[A-Za-z0-9]+)\s+to\s+(?<toQty>[\d.,]+)\s+(?<to>[A-Za-z0-9]+)", RegexOptions.IgnoreCase)]
    private static partial Regex ConvertNote();

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NonWord();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Squeeze();
}
