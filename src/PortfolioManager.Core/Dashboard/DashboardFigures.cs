using PortfolioManager.Core.Holdings;
using PortfolioManager.Core.Payouts;
using PortfolioManager.Core.Performance;
using PortfolioManager.Core.Regions;
using PortfolioManager.Core.Tax;

namespace PortfolioManager.Core.Dashboard;

/// <summary>The latest day the shares' value moved: the change in dollars (money added or taken out left out) and as a return.</summary>
public sealed record DayMove(DateOnly Date, decimal Change, double Return);

/// <summary>One open holding for the dashboard's top holdings table.</summary>
public sealed record TopHolding(Holding Holding, decimal Weight)
{
    /// <summary>Gain against cost, or null when there's no cost to compare with.</summary>
    public decimal? GainPercent => Holding.Cost > 0 ? Holding.CapitalGain / Holding.Cost : null;
}

public sealed record AllocationSlice(string Label, decimal Value, decimal Share);

/// <summary>Income for one calendar month: what was received, split by kind, and what's projected from last year's payouts.</summary>
public sealed record IncomeMonth(DateTime Month, decimal Franked, decimal Unfranked, decimal Other, decimal Projected)
{
    public decimal Received => Franked + Unfranked + Other;
}

/// <summary>A BAS (or other sales tax return) period and the usual date it's due.</summary>
public sealed record BasPeriod(DateOnly From, DateOnly To, DateOnly Due);

/// <summary>Something on the dashboard's "Needs attention" list. Page is a Razor page path such as /Accounting/Unallocated.</summary>
public sealed record AttentionItem(string Text, string Page, bool Urgent = false);

/// <summary>A choice on the value chart's period picker.</summary>
public sealed record ChartPeriod(string Key, string Label);

/// <summary>The pure working behind the dashboard's cards, kept here so it can be tested without a database.</summary>
public static class DashboardFigures
{
    public const string DefaultPeriod = "FYTD";

    /// <summary>A price older than this many days is flagged (covers a weekend and a public holiday).</summary>
    public const int StalePriceDays = 5;

    /// <summary>How close to the year end the "sell losers before the year ends?" hint shows.</summary>
    public const int HarvestDays = 60;

    public static readonly IReadOnlyList<ChartPeriod> Periods =
    [
        new("1M", "1M"), new("3M", "3M"), new("FYTD", "This FY"), new("1Y", "1Y"), new("5Y", "5Y"), new("ALL", "All"),
    ];

    private static readonly HashSet<string> AustralianExchanges = new(StringComparer.OrdinalIgnoreCase) { "ASX", "CXA", "NSX", "CHIA" };

    /// <summary>The first day of a chart period. Unknown keys fall back to this financial year. "ALL" goes back about 15 years.</summary>
    public static DateOnly PeriodStart(string? key, DateOnly today, YearStart? yearStart = null) => key?.ToUpperInvariant() switch
    {
        "1M" => today.AddMonths(-1),
        "3M" => today.AddMonths(-3),
        "1Y" => today.AddYears(-1),
        "5Y" => today.AddYears(-5),
        "ALL" => today.AddDays(-5500),
        _ => FinancialYear.StartDate(FinancialYear.EndYearFor(today, yearStart), yearStart),
    };

    public static string NormalPeriod(string? key) =>
        Periods.FirstOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase))?.Key ?? DefaultPeriod;

    /// <summary>Drops the days before anything was held, so "All" starts at the first trade instead of a long flat line at nil.</summary>
    public static IReadOnlyList<PerformanceDay> SinceFirstHolding(IReadOnlyList<PerformanceDay> days)
    {
        var first = 0;
        while (first < days.Count - 1 && days[first].Value == 0 && days[first].MoneyIn == 0 && days[first].MarketValue == 0)
            first++;
        return first == 0 ? days : days.Skip(first).ToList();
    }

    /// <summary>Indexes of the days to chart: every day, or every week for long periods so the page stays light. The last day is always in.</summary>
    public static IReadOnlyList<int> ChartIndexes(int count)
    {
        var step = count > 800 ? 7 : 1;
        var list = new List<int>();
        for (var i = 0; i < count; i += step)
            list.Add(i);
        if (count > 0 && list[^1] != count - 1)
            list.Add(count - 1);
        return list;
    }

    /// <summary>
    /// The latest day the market value moved from the day before (so a weekend shows Friday's move), looking back up to ten days.
    /// The change follows the daily return: value + money taken out + income - money added - yesterday's value.
    /// </summary>
    public static DayMove? LatestMove(IReadOnlyList<PerformanceDay> days)
    {
        for (var i = days.Count - 1; i >= 1 && i >= days.Count - 10; i--)
        {
            var d = days[i];
            var before = days[i - 1];
            if (d.MarketValue == before.MarketValue)
                continue;
            var change = d.Value + d.MoneyOut + d.Income - d.MoneyIn - before.Value;
            return new DayMove(d.Date, change, d.Return);
        }
        return null;
    }

    /// <summary>The biggest open holdings by market value, with each one's share of all open holdings.</summary>
    public static IReadOnlyList<TopHolding> Top(IEnumerable<Holding> holdings, int count = 10)
    {
        var open = holdings.Where(h => h.Units > 0 && h.MarketValue > 0).ToList();
        var total = open.Sum(h => h.MarketValue);
        return open.OrderByDescending(h => h.MarketValue)
            .Take(count)
            .Select(h => new TopHolding(h, total > 0 ? h.MarketValue / total : 0))
            .ToList();
    }

    /// <summary>
    /// What the money is in: Australian and overseas listed investments (by exchange), crypto, property equity and cash.
    /// Outside Australia the listed investments are one slice. Only positive amounts are charted.
    /// </summary>
    public static IReadOnlyList<AllocationSlice> ByType(IEnumerable<Holding> holdings, decimal cash, decimal propertyEquity, bool australia)
    {
        var amounts = new Dictionary<string, decimal>();
        void Add(string label, decimal value) => amounts[label] = amounts.GetValueOrDefault(label) + value;

        foreach (var h in holdings.Where(h => h.Units > 0))
        {
            var exchange = h.ExchangeCode.Trim();
            if (exchange.Equals("CRYPTO", StringComparison.OrdinalIgnoreCase))
                Add("Crypto", h.MarketValue);
            else if (exchange.Equals(PerformanceCalculator.ForexExchange, StringComparison.OrdinalIgnoreCase))
                Add("Cash", h.MarketValue);
            else if (!australia)
                Add("Shares and funds", h.MarketValue);
            else
                Add(AustralianExchanges.Contains(exchange) ? "Australian shares and funds" : "Overseas shares and funds", h.MarketValue);
        }
        Add("Property equity", propertyEquity);
        Add("Cash", cash);

        var positive = amounts.Where(a => a.Value > 0).ToList();
        var total = positive.Sum(a => a.Value);
        return positive.OrderByDescending(a => a.Value)
            .Select(a => new AllocationSlice(a.Key, a.Value, total > 0 ? a.Value / total : 0))
            .ToList();
    }

    /// <summary>
    /// Twelve months back (this month included) of income received, split franked, unfranked and other (interest, trust and foreign income),
    /// then the projected payouts for the twelve months after. Rental payouts are left out: they're on the property card.
    /// </summary>
    public static IReadOnlyList<IncomeMonth> IncomeMonths(IEnumerable<Payout> received, IEnumerable<ProjectedPayout> projected, DateOnly today)
    {
        var thisMonth = new DateTime(today.Year, today.Month, 1);
        var months = Enumerable.Range(-11, 24).Select(i => thisMonth.AddMonths(i)).ToList();
        static DateTime MonthOf(DateTime d) => new(d.Year, d.Month, 1);

        var got = received.Where(p => p.Date is not null && !p.IsRental)
            .GroupBy(p => MonthOf(p.Date!.Value))
            .ToDictionary(g => g.Key, g => (Franked: g.Sum(p => p.Franked), Unfranked: g.Sum(p => p.Unfranked),
                Other: g.Sum(p => Math.Max(0, p.Total - p.Franked - p.Unfranked))));
        var expected = projected
            .GroupBy(p => MonthOf(p.ProjectedPaymentDate))
            .ToDictionary(g => g.Key, g => g.Sum(p => p.ProjectedAmount));

        return months.Select(m =>
        {
            var r = got.GetValueOrDefault(m);
            // Past months show only what came in; the projection starts this month
            var p = m >= thisMonth ? expected.GetValueOrDefault(m) : 0;
            return new IncomeMonth(m, r.Franked, r.Unfranked, r.Other, p);
        }).ToList();
    }

    /// <summary>
    /// The next BAS due on or after today, for periods of 1, 2, 3, 6 or 12 months lined up with a July financial year.
    /// Usual ATO dates: monthly the 21st of the next month; quarterly the 28th of the month after the quarter, except
    /// the December quarter (28 February); yearly 31 October. Two- and six-monthly use the 28th of the next month.
    /// Agents lodging online often get longer, so this is a reminder, not the exact date.
    /// </summary>
    public static BasPeriod? NextBas(DateOnly today, int months)
    {
        if (months is not (1 or 2 or 3 or 6 or 12))
            return null;
        // Period ends are the month ends where the months since 30 June divide evenly; look back far enough to catch a due date still to come
        for (var back = 14; back >= -12; back--)
        {
            var end = new DateOnly(today.Year, today.Month, 1).AddMonths(-back).AddDays(-1);
            var monthsSinceJune = ((end.Month - 6) % 12 + 12) % 12;
            if (monthsSinceJune % months != 0)
                continue;
            var from = end.AddDays(1).AddMonths(-months);
            var due = Due(end, months);
            if (due >= today)
                return new BasPeriod(from, end, due);
        }
        return null;
    }

    private static DateOnly Due(DateOnly periodEnd, int months)
    {
        var next = periodEnd.AddDays(1);
        return months switch
        {
            1 => new DateOnly(next.Year, next.Month, 21),
            12 => new DateOnly(periodEnd.Year, 10, 31),
            3 when periodEnd.Month == 12 => new DateOnly(periodEnd.Year + 1, 2, 28),
            _ => new DateOnly(next.Year, next.Month, 28),
        };
    }

    /// <summary>Open holdings whose latest price is older than <see cref="StalePriceDays"/>, or that have no price at all.</summary>
    public static IReadOnlyList<Holding> StalePrices(IEnumerable<Holding> holdings, IReadOnlyDictionary<string, DateTime> lastPrice, DateOnly today)
    {
        var cutoff = today.AddDays(-StalePriceDays).ToDateTime(TimeOnly.MinValue);
        return holdings
            .Where(h => h.Units > 0 && !h.ExchangeCode.Equals(PerformanceCalculator.ForexExchange, StringComparison.OrdinalIgnoreCase))
            .Where(h => !lastPrice.TryGetValue(PriceKey(h.ExchangeCode, h.StockCode), out var d) || d < cutoff)
            .ToList();
    }

    public static string PriceKey(string exchange, string code) => $"{exchange.Trim().ToUpperInvariant()}:{code.Trim().ToUpperInvariant()}";

    /// <summary>Open holdings with no cost recorded, which makes their gains and tax wrong.</summary>
    public static IReadOnlyList<Holding> MissingCost(IEnumerable<Holding> holdings) =>
        holdings.Where(h => h.Units > 0 && h.Cost <= 0 && h.MarketValue > 0).ToList();

    /// <summary>
    /// Near the year end, the unrealised losses that could be sold to offset this year's gains: the total and how many holdings.
    /// Null when it's not near the year end or there are no losses.
    /// </summary>
    public static (decimal Losses, int Holdings, DateOnly YearEnd)? HarvestHint(IEnumerable<Holding> holdings, DateOnly today, YearStart? yearStart = null)
    {
        var yearEnd = FinancialYear.EndDate(FinancialYear.EndYearFor(today, yearStart), yearStart);
        if (yearEnd.DayNumber - today.DayNumber > HarvestDays)
            return null;
        var losers = holdings.Where(h => h.Units > 0 && h.Cost > 0 && h.MarketValue < h.Cost).ToList();
        if (losers.Count == 0)
            return null;
        return (losers.Sum(h => h.Cost - h.MarketValue), losers.Count, yearEnd);
    }
}
