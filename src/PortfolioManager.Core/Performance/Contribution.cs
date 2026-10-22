using Dapper;
using PortfolioManager.Core.Data;

namespace PortfolioManager.Core.Performance;

/// <summary>Income paid by one holding (cash or reinvested), in the portfolio's currency.</summary>
public sealed record PerfHoldingIncome(string Exchange, string Code, DateOnly Date, decimal Amount);

/// <summary>
/// What one holding added to (or took from) the portfolio over a period, in the portfolio's currency.
/// PriceGain + CurrencyGain + Income = TotalGain. NetBought is money put in by buying less money taken out by selling.
/// </summary>
public sealed record HoldingContribution(
    string Exchange, string Code, string Currency,
    double StartUnits, decimal StartValue, double EndUnits, decimal EndValue, decimal NetBought,
    decimal PriceGain, decimal CurrencyGain, decimal Income, decimal AverageInvested)
{
    public decimal TotalGain => PriceGain + CurrencyGain + Income;

    /// <summary>The holding's own return: total gain over its average invested value (the start value plus each
    /// purchase less each sale, weighted by the part of the period it was held).</summary>
    public double? Return => AverageInvested > 0 ? (double)(TotalGain / AverageInvested) : null;

    /// <summary>The holding's part of the portfolio's return: its gain over the portfolio's average invested value.</summary>
    public double? ShareOfReturn { get; init; }
}

public sealed record ContributionReport(
    DateOnly From, DateOnly To, string Currency, IReadOnlyList<HoldingContribution> Holdings, IReadOnlyList<string> Warnings)
{
    public decimal PriceGain => Holdings.Sum(h => h.PriceGain);
    public decimal CurrencyGain => Holdings.Sum(h => h.CurrencyGain);
    public decimal Income => Holdings.Sum(h => h.Income);
    public decimal TotalGain => Holdings.Sum(h => h.TotalGain);
    public decimal StartValue => Holdings.Sum(h => h.StartValue);
    public decimal EndValue => Holdings.Sum(h => h.EndValue);
    public decimal NetBought => Holdings.Sum(h => h.NetBought);
    public decimal AverageInvested => Holdings.Sum(h => h.AverageInvested);
    public double? Return => AverageInvested > 0 ? (double)(TotalGain / AverageInvested) : null;
}

/// <summary>
/// Splits each holding's gain over a period into price, currency and income. A foreign holding's price gain is its
/// change in its own currency converted at the end rate; the currency gain is what the rate moved on the start value
/// and on each purchase or sale since. Order totals are in the trade's currency.
/// </summary>
public static class ContributionCalculator
{
    public static ContributionReport Calculate(PerformanceInput input, IReadOnlyList<PerfHoldingIncome> income, DateOnly from, DateOnly to)
    {
        if (to < from)
            (from, to) = (to, from);
        var ccy = PerformanceCalculator.Norm(input.Currency) is { Length: > 0 } c ? c : "AUD";
        var prices = new PriceBook(input.Prices, input.Splits);
        var warnings = new SortedSet<string>(StringComparer.Ordinal);
        var before = from.AddDays(-1);
        var span = Math.Max(1, to.DayNumber - before.DayNumber);

        decimal Fx(string currency, DateOnly day)
        {
            if (currency.Length == 0 || currency == ccy)
                return 1m;
            if (prices.Fx(currency, ccy, day) is { } rate)
                return (decimal)rate;
            warnings.Add($"No {currency} to {ccy} exchange rate on file, so {currency} amounts are counted one for one.");
            return 1m;
        }

        var incomeBy = income.Where(i => i.Date >= from && i.Date <= to)
            .GroupBy(i => (PerformanceCalculator.Norm(i.Exchange), PerformanceCalculator.Norm(i.Code)))
            .ToDictionary(g => g.Key, g => g.Sum(i => i.Amount));

        var rows = new List<HoldingContribution>();
        foreach (var g in input.Trades.Where(t => t.Date <= to && PerformanceCalculator.Side(t.Type) != 0)
                     .GroupBy(t => (PerformanceCalculator.Norm(t.Exchange), PerformanceCalculator.Norm(t.Code))))
        {
            var (exchange, code) = g.Key;
            var trades = g.OrderBy(t => t.Date).ToList();
            var cur = trades.Select(t => PerformanceCalculator.Norm(t.Currency)).LastOrDefault(x => x.Length > 0) ?? ccy;
            double Units(DateOnly day) => trades.Where(t => t.Date <= day).Sum(t => PerformanceCalculator.Side(t.Type) * t.Quantity);
            double Price(DateOnly day)
            {
                var last = trades.LastOrDefault(t => t.Date <= day && t.PricePerUnit > 0)?.PricePerUnit ?? 0;
                if (prices.Close(exchange, code, day) is { } close)
                    return close;
                warnings.Add($"{exchange}:{code} has no market price on file for part of the period, so its last trade price is used.");
                return last;
            }

            var startUnits = Units(before);
            var endUnits = Units(to);
            var during = trades.Where(t => t.Date >= from).ToList();
            var paid = incomeBy.GetValueOrDefault(g.Key);
            if (Math.Abs(startUnits) < 1e-9 && during.Count == 0 && paid == 0)
                continue;

            var startLocal = Math.Abs(startUnits) < 1e-9 ? 0m : (decimal)(startUnits * Price(before));
            var endLocal = Math.Abs(endUnits) < 1e-9 ? 0m : (decimal)(endUnits * Price(to));
            var (startFx, endFx) = (Fx(cur, before), Fx(cur, to));

            decimal flowsLocal = 0, flowsAtTheirRate = 0, weighted = 0;
            foreach (var t in during)
            {
                var local = PerformanceCalculator.Side(t.Type) * Math.Abs(t.OrderTotal);
                var converted = local * Fx(cur, t.Date);
                flowsLocal += local;
                flowsAtTheirRate += converted;
                weighted += converted * (to.DayNumber - t.Date.DayNumber + 1) / span;
            }

            var startValue = Round(startLocal * startFx);
            var endValue = Round(endLocal * endFx);
            var priceGain = Round((endLocal - startLocal - flowsLocal) * endFx);
            var currencyGain = Round(endValue - startValue - flowsAtTheirRate) - priceGain;
            rows.Add(new HoldingContribution(exchange, code, cur, startUnits, startValue, endUnits, endValue, Round(flowsAtTheirRate),
                priceGain, currencyGain, Round(paid), Round(startValue + weighted)));
        }

        // Each holding's part of the share portfolio's return, over the holdings' combined average invested value
        var total = rows.Sum(r => r.AverageInvested);
        var list = rows
            .Select(r => r with { ShareOfReturn = total > 0 ? (double)(r.TotalGain / total) : null })
            .OrderByDescending(r => r.TotalGain).ToList();
        return new ContributionReport(from, to, ccy, list, warnings.ToList());
    }

    private static decimal Round(decimal d) => Math.Round(d, 2, MidpointRounding.AwayFromZero);
}
