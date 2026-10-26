using PortfolioManager.Core.Performance;
using PortfolioManager.Core.Reports;

namespace PortfolioManager.Tests;

public class GroupReturnTests
{
    private static readonly DateOnly Day1 = new(2026, 7, 1);

    private static PerformanceResult Entity(decimal start, params (decimal Value, decimal In, decimal Out)[] days)
    {
        var list = new List<PerformanceDay>();
        var previous = start;
        var growth = 1d;
        for (var i = 0; i < days.Length; i++)
        {
            var (v, mi, mo) = days[i];
            var r = PerformanceCalculator.DailyReturn(previous, v, mi, mo, 0);
            growth *= 1 + r;
            list.Add(new PerformanceDay(Day1.AddDays(i), 0, v, v, mi, mo, 0, r, growth - 1));
            previous = v;
        }
        var summary = new PerformanceSummary(Day1, Day1.AddDays(days.Length - 1), start, days[^1].Value, list.Sum(d => d.MoneyIn), list.Sum(d => d.MoneyOut), 0, growth - 1, null);
        return new PerformanceResult(summary, list, PerformanceCalculator.Monthly(list), []);
    }

    [Fact]
    public void Group_return_weights_each_entity_by_its_value()
    {
        // $1,000 up 10% and $3,000 up 0%: the group is up 2.5%
        var a = Entity(1000, (1100, 0, 0));
        var b = Entity(3000, (3000, 0, 0));
        var g = GroupReturns.Combine([a, b])!;
        Assert.Equal(0.025, g.Summary.TimeWeightedReturn, 6);
        Assert.Equal((4000m, 4100m), (g.Summary.StartValue, g.Summary.EndValue));
    }

    [Fact]
    public void Money_moved_between_entities_isnt_growth()
    {
        // The trust pays $500 to the individual: out of one, into the other, the same day
        var trust = Entity(2000, (1500, 0, 500));
        var person = Entity(1000, (1500, 500, 0));
        var g = GroupReturns.Combine([trust, person])!;
        Assert.Equal(0, g.Summary.TimeWeightedReturn, 9);
        Assert.Single(g.Months);
    }

    [Fact]
    public void Nothing_to_combine_is_null() => Assert.Null(GroupReturns.Combine([]));
}
