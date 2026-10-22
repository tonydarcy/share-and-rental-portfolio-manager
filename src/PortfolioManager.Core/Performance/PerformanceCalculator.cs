namespace PortfolioManager.Core.Performance;

/// <summary>A share trade as the performance calculation needs it. OrderTotal is in the portfolio's currency.</summary>
public sealed record PerfTrade(
    DateOnly Date,
    string Exchange,
    string Code,
    string Type,
    double Quantity,
    decimal OrderTotal,
    double PricePerUnit,
    string Currency,
    bool HasCash,
    bool IsReinvestment);

/// <summary>A bank row. FromTrade rows are a trade's settlement, so they move money inside the portfolio.</summary>
public sealed record PerfCash(DateOnly Date, decimal Deposit, decimal Withdrawal, string Currency, bool FromTrade);

/// <summary>A closing price (or an FX rate when Exchange is FOREX).</summary>
public sealed record PerfPrice(string Exchange, string Code, DateOnly Date, double Close);

/// <summary>A split or consolidation: from Date the holding is on the new basis (Ratio new units per old unit).</summary>
public sealed record PerfSplit(string Exchange, string Code, DateOnly Date, double Ratio);

/// <summary>Cash income from a payout that was not reinvested.</summary>
public sealed record PerfIncome(DateOnly Date, decimal Amount);

public sealed record PerformanceInput(
    string Currency,
    IReadOnlyList<PerfTrade> Trades,
    IReadOnlyList<PerfCash> Cash,
    IReadOnlyList<PerfPrice> Prices,
    IReadOnlyList<PerfSplit> Splits,
    IReadOnlyList<PerfIncome> Income);

/// <summary>One day's value. Return is that day's time-weighted return; Cumulative is the return since the period began.</summary>
public sealed record PerformanceDay(
    DateOnly Date,
    decimal Cash,
    decimal MarketValue,
    decimal Value,
    decimal MoneyIn,
    decimal MoneyOut,
    decimal Income,
    double Return,
    double Cumulative);

public sealed record PeriodReturn(DateOnly From, DateOnly To, double Return);

public sealed record PerformanceSummary(
    DateOnly From,
    DateOnly To,
    decimal StartValue,
    decimal EndValue,
    decimal MoneyIn,
    decimal MoneyOut,
    decimal Income,
    double TimeWeightedReturn,
    double? AnnualisedReturn)
{
    public decimal NetMoneyAdded => MoneyIn - MoneyOut;

    /// <summary>Change in value not explained by money added or taken out.</summary>
    public decimal Growth => EndValue - StartValue - NetMoneyAdded;

    public decimal TotalGain => Growth + Income;
}

public sealed record PerformanceResult(
    PerformanceSummary Summary,
    IReadOnlyList<PerformanceDay> Days,
    IReadOnlyList<PeriodReturn> Months,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Works out a portfolio's daily value and time-weighted return (TWR) from its trades, cash and prices.
/// <para>
/// Value = cash balance + market value of the shares held that day. Money added or taken out is a
/// cash deposit or withdrawal that isn't a trade's settlement, or a trade with no cash recorded
/// (shares transferred in or out). Income is a payout that wasn't reinvested; it counts as return
/// whether or not it was also entered as a cash deposit.
/// </para>
/// <para>
/// Each day's return is (value + money out + income - money in) / yesterday's value - 1, and the
/// days chain together (see <see cref="DailyReturn"/>).
/// </para>
/// </summary>
public static class PerformanceCalculator
{
    public const string ForexExchange = "FOREX";

    public static PerformanceResult Calculate(PerformanceInput input, DateOnly from, DateOnly to)
    {
        if (to < from)
            (from, to) = (to, from);

        var ccy = Norm(input.Currency) is { Length: > 0 } c ? c : "AUD";
        var warnings = new SortedSet<string>(StringComparer.Ordinal);
        var prices = new PriceBook(input.Prices, input.Splits);

        var trades = input.Trades.OrderBy(t => t.Date).ToList();
        var cash = input.Cash.OrderBy(c => c.Date).ToList();
        var incomeByDay = input.Income.GroupBy(i => i.Date).ToDictionary(g => g.Key, g => g.Sum(i => i.Amount));

        var holdings = new Dictionary<(string, string), Holding>();
        var cashByCcy = new Dictionary<string, decimal>(StringComparer.Ordinal);
        int ti = 0, ci = 0;

        decimal Fx(string currency, DateOnly day)
        {
            var from3 = Norm(currency);
            if (from3.Length == 0 || from3 == ccy)
                return 1m;
            if (prices.Fx(from3, ccy, day) is { } rate)
                return (decimal)rate;
            warnings.Add($"No {from3} to {ccy} exchange rate on file, so {from3} amounts are counted one for one.");
            return 1m;
        }

        // Applies trades and cash up to and including the day, returning the money moved in and out that day
        (decimal In, decimal Out) Advance(DateOnly day)
        {
            decimal moneyIn = 0, moneyOut = 0;
            for (; ti < trades.Count && trades[ti].Date <= day; ti++)
            {
                var t = trades[ti];
                var sign = Side(t.Type);
                if (sign == 0)
                    continue;
                var key = (Norm(t.Exchange), Norm(t.Code));
                if (!holdings.TryGetValue(key, out var h))
                    holdings[key] = h = new Holding(key.Item1, key.Item2);
                h.Quantity += sign * t.Quantity;
                if (t.PricePerUnit > 0)
                    h.LastTradePrice = t.PricePerUnit;
                if (Norm(t.Currency) is { Length: > 0 } tc)
                    h.Currency = tc;

                if (t.Date == day && !t.HasCash && !t.IsReinvestment)
                {
                    if (sign > 0) moneyIn += Math.Abs(t.OrderTotal);
                    else moneyOut += Math.Abs(t.OrderTotal);
                }
            }

            for (; ci < cash.Count && cash[ci].Date <= day; ci++)
            {
                var c = cash[ci];
                var cc = Norm(c.Currency) is { Length: > 0 } x ? x : ccy;
                cashByCcy[cc] = cashByCcy.GetValueOrDefault(cc) + c.Deposit - c.Withdrawal;
                if (c.Date == day && !c.FromTrade)
                {
                    var fx = Fx(cc, day);
                    moneyIn += c.Deposit * fx;
                    moneyOut += c.Withdrawal * fx;
                }
            }
            return (moneyIn, moneyOut);
        }

        (decimal Cash, decimal Market) Value(DateOnly day)
        {
            decimal cashValue = cashByCcy.Sum(kv => kv.Value * Fx(kv.Key, day));
            decimal market = 0;
            foreach (var h in holdings.Values)
            {
                if (Math.Abs(h.Quantity) < 1e-9)
                    continue;
                var price = prices.Close(h.Exchange, h.Code, day);
                if (price is null)
                {
                    price = h.LastTradePrice;
                    warnings.Add($"{h.Exchange}:{h.Code} has no market price on file for part of the period, so its last trade price is used.");
                }
                market += (decimal)(h.Quantity * price.Value) * Fx(h.Currency, day);
            }
            return (Round(cashValue), Round(market));
        }

        // Value at the end of the day before the period starts
        var before = from.AddDays(-1);
        Advance(before);
        var (startCash, startMarket) = Value(before);
        var previous = startCash + startMarket;

        var days = new List<PerformanceDay>();
        double growth = 1;
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            var (moneyIn, moneyOut) = Advance(day);
            var (dayCash, dayMarket) = Value(day);
            var value = dayCash + dayMarket;
            var income = incomeByDay.GetValueOrDefault(day);

            var r = DailyReturn(previous, value, moneyIn, moneyOut, income);
            growth *= 1 + r;
            days.Add(new PerformanceDay(day, dayCash, dayMarket, value, Round(moneyIn), Round(moneyOut), income, r, growth - 1));
            previous = value;
        }

        var twr = growth - 1;
        var span = to.DayNumber - from.DayNumber + 1;
        double? annualised = span > 366 && growth > 0 ? Math.Pow(growth, 365.25 / span) - 1 : null;

        var summary = new PerformanceSummary(
            from, to, startCash + startMarket, days[^1].Value,
            days.Sum(d => d.MoneyIn), days.Sum(d => d.MoneyOut), days.Sum(d => d.Income), twr, annualised);

        return new PerformanceResult(summary, days, Monthly(days), warnings.ToList());
    }

    /// <summary>
    /// One day's return: (value + money out + income - money in) / yesterday's value - 1, so money moving
    /// in or out that day isn't counted as growth. On the first day there is anything invested, the money
    /// added that day is the base instead. Zero while there is nothing invested.
    /// </summary>
    public static double DailyReturn(decimal previousValue, decimal value, decimal moneyIn, decimal moneyOut, decimal income)
    {
        if (previousValue >= 0.01m)
            return (double)((value + moneyOut + income - moneyIn) / previousValue) - 1;
        if (moneyIn >= 0.01m)
            return (double)((value + moneyOut + income) / moneyIn) - 1;
        return 0;
    }

    /// <summary>Chains daily returns within each calendar month.</summary>
    public static IReadOnlyList<PeriodReturn> Monthly(IReadOnlyList<PerformanceDay> days) =>
        days.GroupBy(d => (d.Date.Year, d.Date.Month))
            .Select(g => new PeriodReturn(g.First().Date, g.Last().Date, g.Aggregate(1d, (acc, d) => acc * (1 + d.Return)) - 1))
            .ToList();

    /// <summary>+1 for a buy, -1 for a sell, 0 for anything else.</summary>
    public static int Side(string? type) => Norm(type) switch
    {
        "BUY" => 1,
        "SELL" => -1,
        _ => 0,
    };

    internal static string Norm(string? s) => (s ?? "").Trim().ToUpperInvariant();

    private static decimal Round(decimal d) => Math.Round(d, 2, MidpointRounding.AwayFromZero);

    private sealed class Holding(string exchange, string code)
    {
        public string Exchange { get; } = exchange;
        public string Code { get; } = code;
        public double Quantity { get; set; }
        public double LastTradePrice { get; set; }
        public string Currency { get; set; } = "";
    }
}

/// <summary>Closing prices by stock, looked up as the latest on or before a day, in today's units after any split.</summary>
public sealed class PriceBook
{
    private readonly Dictionary<(string, string), (DateOnly[] Dates, double[] Closes)> _series;
    private readonly Dictionary<(string, string), PerfSplit[]> _splits;

    public PriceBook(IEnumerable<PerfPrice> prices, IEnumerable<PerfSplit> splits)
    {
        _series = prices
            .Where(p => p.Close > 0)
            .GroupBy(p => (PerformanceCalculator.Norm(p.Exchange), Key(p.Exchange, p.Code)))
            .ToDictionary(g => g.Key, g =>
            {
                // One price per day; the last one given wins
                var byDay = new SortedDictionary<DateOnly, double>();
                foreach (var p in g)
                    byDay[p.Date] = p.Close;
                return (byDay.Keys.ToArray(), byDay.Values.ToArray());
            });
        _splits = splits
            .Where(s => s.Ratio > 0)
            .GroupBy(s => (PerformanceCalculator.Norm(s.Exchange), PerformanceCalculator.Norm(s.Code)))
            .ToDictionary(g => g.Key, g => g.ToArray());
    }

    /// <summary>
    /// The latest close on or before the day. Trades before a split are restated in the new units, so a
    /// price from before the split is divided by its ratio to match.
    /// </summary>
    public double? Close(string exchange, string code, DateOnly day)
    {
        var key = (PerformanceCalculator.Norm(exchange), PerformanceCalculator.Norm(code));
        if (Latest(key, day) is not { } found)
            return null;
        var close = found.Close;
        if (_splits.TryGetValue(key, out var splits))
            foreach (var s in splits)
                if (s.Date > found.Date)
                    close /= s.Ratio;
        return close;
    }

    /// <summary>Units of the portfolio currency per unit of a foreign one, from a USD/AUD style pair or its inverse.</summary>
    public double? Fx(string fromCurrency, string toCurrency, DateOnly day)
    {
        var ex = PerformanceCalculator.ForexExchange;
        if (Latest((ex, fromCurrency + toCurrency), day) is { } direct)
            return direct.Close;
        if (Latest((ex, toCurrency + fromCurrency), day) is { } inverse)
            return 1 / inverse.Close;
        return null;
    }

    private (DateOnly Date, double Close)? Latest((string, string) key, DateOnly day)
    {
        if (!_series.TryGetValue(key, out var s))
            return null;
        var i = Array.BinarySearch(s.Dates, day);
        if (i < 0)
            i = ~i - 1;
        return i >= 0 ? (s.Dates[i], s.Closes[i]) : null;
    }

    // FX pairs are stored as 'USD/AUD'; compare them without the slash
    private static string Key(string exchange, string code)
    {
        var c = PerformanceCalculator.Norm(code);
        return PerformanceCalculator.Norm(exchange) == PerformanceCalculator.ForexExchange ? c.Replace("/", "") : c;
    }
}
