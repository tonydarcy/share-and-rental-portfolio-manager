using System.Data;
using System.Xml.Linq;
using Dapper;
using PortfolioManager.Core.Dashboard;
using PortfolioManager.Core.Data;
using PortfolioManager.Core.Holdings;
using PortfolioManager.Core.Performance;
using PortfolioManager.Core.Tax;

namespace PortfolioManager.Core.Reports;

/// <summary>A login's named group of portfolios, such as a family's individual, trust, company and SMSF.</summary>
public sealed record FamilyGroup(int GroupId, string Name, IReadOnlyList<int> PortfolioIds);

/// <summary>One entity's figures for the consolidated view, in its own currency.</summary>
public sealed record EntityFigures(int PortfolioId, string Name, string EntityType, string Currency,
    decimal ShareValue, decimal CashValue, decimal Income, decimal NetCapitalGain, IReadOnlyList<Holding> Holdings)
{
    public decimal TotalValue => ShareValue + CashValue;
}

/// <summary>A stock held by one or more of the group's entities: the value each holds.</summary>
public sealed record ConsolidatedHolding(string ExchangeCode, string StockCode, IReadOnlyDictionary<int, decimal> ValueByEntity,
    IReadOnlyDictionary<int, decimal> UnitsByEntity)
{
    public decimal Value => ValueByEntity.Values.Sum();
    public decimal Units => UnitsByEntity.Values.Sum();
}

public sealed record ConsolidatedReport(IReadOnlyList<EntityFigures> Entities, IReadOnlyList<ConsolidatedHolding> Holdings)
{
    /// <summary>Totals only add up when every entity uses the same currency.</summary>
    public bool OneCurrency => Entities.Select(e => e.Currency).Distinct(StringComparer.OrdinalIgnoreCase).Count() <= 1;
    public decimal TotalValue => Entities.Sum(e => e.TotalValue);
    public decimal ShareValue => Entities.Sum(e => e.ShareValue);
    public decimal CashValue => Entities.Sum(e => e.CashValue);
    public decimal Income => Entities.Sum(e => e.Income);
    public decimal NetCapitalGain => Entities.Sum(e => e.NetCapitalGain);

    public double? ShareOf(decimal part) => OneCurrency && TotalValue != 0 ? (double)(part / TotalValue) : null;

    public static ConsolidatedReport Build(IReadOnlyList<EntityFigures> entities)
    {
        var holdings = entities
            .SelectMany(e => e.Holdings.Where(h => h.Units != 0).Select(h => (e.PortfolioId, h)))
            .GroupBy(x => (x.h.ExchangeCode.Trim().ToUpperInvariant(), x.h.StockCode.Trim().ToUpperInvariant()))
            .Select(g => new ConsolidatedHolding(g.Key.Item1, g.Key.Item2,
                g.GroupBy(x => x.PortfolioId).ToDictionary(x => x.Key, x => x.Sum(y => y.h.MarketValue)),
                g.GroupBy(x => x.PortfolioId).ToDictionary(x => x.Key, x => x.Sum(y => y.h.Units))))
            .OrderByDescending(h => h.Value).ThenBy(h => h.StockCode)
            .ToList();
        return new ConsolidatedReport(entities, holdings);
    }
}

/// <summary>One entity's return over the period, and the group's.</summary>
public sealed record GroupPerformance(PerformanceResult? Group, IReadOnlyList<(int PortfolioId, PerformanceSummary Summary)> Entities,
    IReadOnlyList<string> Warnings);

/// <summary>
/// The group's time-weighted return: each day's values, money in and out and income are added across the entities, then
/// the days are chained as for one portfolio (<see cref="PerformanceCalculator.DailyReturn"/>). Money moved between two of
/// the group's entities shows as money out of one and into the other, so it isn't counted as growth.
/// </summary>
public static class GroupReturns
{
    public static PerformanceResult? Combine(IReadOnlyList<PerformanceResult> entities)
    {
        var withDays = entities.Where(e => e.Days.Count > 0).ToList();
        if (withDays.Count == 0)
            return null;
        var dates = withDays.SelectMany(e => e.Days.Select(d => d.Date)).Distinct().Order().ToList();
        var byDate = withDays.Select(e => e.Days.ToDictionary(d => d.Date)).ToList();
        var last = new PerformanceDay?[withDays.Count];
        var days = new List<PerformanceDay>(dates.Count);
        var growth = 1d;
        // The group's value the day before the period starts
        var previous = withDays.Sum(e => e.Summary.StartValue);
        foreach (var date in dates)
        {
            decimal cash = 0, market = 0, value = 0, moneyIn = 0, moneyOut = 0, income = 0;
            for (var i = 0; i < withDays.Count; i++)
            {
                if (byDate[i].TryGetValue(date, out var d))
                {
                    last[i] = d;
                    moneyIn += d.MoneyIn;
                    moneyOut += d.MoneyOut;
                    income += d.Income;
                }
                // A day one entity has no row for keeps its last value
                if (last[i] is { } held)
                {
                    cash += held.Cash;
                    market += held.MarketValue;
                    value += held.Value;
                }
            }
            var r = PerformanceCalculator.DailyReturn(previous, value, moneyIn, moneyOut, income);
            growth *= 1 + r;
            days.Add(new PerformanceDay(date, cash, market, value, moneyIn, moneyOut, income, r, growth - 1));
            previous = value;
        }

        var from = withDays.Min(e => e.Summary.From);
        var to = withDays.Max(e => e.Summary.To);
        var span = to.DayNumber - from.DayNumber + 1;
        double? annualised = span > 366 && growth > 0 ? Math.Pow(growth, 365.25 / span) - 1 : null;
        var summary = new PerformanceSummary(from, to, withDays.Sum(e => e.Summary.StartValue), days[^1].Value,
            days.Sum(d => d.MoneyIn), days.Sum(d => d.MoneyOut), days.Sum(d => d.Income), growth - 1, annualised);
        return new PerformanceResult(summary, days, PerformanceCalculator.Monthly(days), withDays.SelectMany(e => e.Warnings).Distinct().ToList());
    }
}

/// <summary>
/// Family groups (Reports > Consolidated) and the figures across them. Groups belong to a login; callers pass the
/// portfolios that login can open, and only those are ever read.
/// </summary>
public sealed class ConsolidatedService(IDbConnectionFactory db, DashboardService dashboard, HoldingsService holdings,
    TaxableIncomeService income, CgtDiscountService cgt, PerformanceService performance)
{
    public const int MaxNameLength = 100;

    public async Task<IReadOnlyList<FamilyGroup>> GetGroupsAsync(int loginId)
    {
        await using var con = db.Create();
        var rows = await con.QueryAsync<(int GroupId, string Name, int? PortfolioId)>("uspGetFamilyGroups", new { iLoginID = loginId },
            commandType: CommandType.StoredProcedure);
        return rows.GroupBy(r => (r.GroupId, r.Name))
            .Select(g => new FamilyGroup(g.Key.GroupId, g.Key.Name, g.Where(r => r.PortfolioId is not null).Select(r => r.PortfolioId!.Value).ToList()))
            .ToList();
    }

    public static string PortfoliosXml(IEnumerable<int> ids) =>
        new XElement("ps", ids.Distinct().Select(id => new XElement("p", new XAttribute("id", id)))).ToString(SaveOptions.DisableFormatting);

    /// <summary>Adds (groupId 0) or changes a group. Portfolios the login can't open are dropped.</summary>
    public async Task<ProcResult> SaveAsync(int loginId, int groupId, string? name, IEnumerable<int> portfolioIds, IReadOnlySet<int> accessible)
    {
        if (string.IsNullOrWhiteSpace(name))
            return ProcResult.Failed("Give the group a name.");
        if (name.Trim().Length > MaxNameLength)
            return ProcResult.Failed($"Keep the name to {MaxNameLength} characters.");
        var ids = portfolioIds.Where(accessible.Contains).Distinct().ToList();
        if (ids.Count < 2)
            return ProcResult.Failed("Pick at least two portfolios.");
        await using var con = db.Create();
        var row = await con.QuerySingleAsync<(string Result, int? Id)>("uspSaveFamilyGroup",
            new { iLoginID = loginId, iFamilyGroupID = groupId, strName = name.Trim(), xmlPortfolios = PortfoliosXml(ids) },
            commandType: CommandType.StoredProcedure);
        return ProcResult.From(row.Result, "Success", row.Id);
    }

    public async Task<ProcResult> DeleteAsync(int loginId, int groupId)
    {
        await using var con = db.Create();
        var result = await con.ExecuteScalarAsync<string>("uspDeleteFamilyGroup", new { iLoginID = loginId, iFamilyGroupID = groupId },
            commandType: CommandType.StoredProcedure);
        return ProcResult.From(result, "Success");
    }

    /// <summary>
    /// Each entity's time-weighted return over the period (up to today) and the group's. The group's return needs every entity
    /// in the same currency; otherwise only each entity's own is given.
    /// </summary>
    public async Task<GroupPerformance> GetPerformanceAsync(IReadOnlyList<Portfolios.PortfolioSummary> portfolios, ReportPeriod period, DateOnly today,
        bool oneCurrency)
    {
        var to = period.To > today ? today : period.To;
        var results = new List<(int, PerformanceResult)>();
        var warnings = new List<string>();
        if (period.From > to)
            return new GroupPerformance(null, [], ["The period hasn't started yet."]);
        foreach (var p in portfolios)
            results.Add((p.PortfolioId, await performance.GetAsync(p.PortfolioId, period.From, to)));
        if (!oneCurrency)
            warnings.Add("The group's return isn't worked out because the entities use different currencies.");
        var group = oneCurrency ? GroupReturns.Combine(results.Select(r => r.Item2).ToList()) : null;
        return new GroupPerformance(group, results.Select(r => (r.Item1, r.Item2.Summary)).ToList(),
            [.. warnings, .. group?.Warnings ?? []]);
    }

    /// <summary>Each entity's value now, and its income and net capital gain for the period.</summary>
    public async Task<ConsolidatedReport> GetAsync(IReadOnlyList<Portfolios.PortfolioSummary> portfolios, ReportPeriod period)
    {
        var entities = new List<EntityFigures>();
        foreach (var p in portfolios)
        {
            var value = await dashboard.GetValueAsync(p.PortfolioId);
            var held = await holdings.GetHoldingsAsync(p.PortfolioId, null, true);
            var paid = await income.GetAsync(p.PortfolioId, period.From, period.To);
            var gains = await cgt.GetReturnAsync(p.PortfolioId, p.EntityType, period);
            entities.Add(new EntityFigures(p.PortfolioId, p.Name, p.EntityType, value.PortfolioCurrency ?? "AUD",
                value.TotalShareValue, value.TotalCashValue, paid.Summary.GrossIncome, gains.Summary.NetCapitalGain, held.Holdings));
        }
        return ConsolidatedReport.Build(entities);
    }
}
