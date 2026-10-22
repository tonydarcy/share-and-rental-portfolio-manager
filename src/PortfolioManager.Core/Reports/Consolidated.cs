using System.Data;
using System.Xml.Linq;
using Dapper;
using PortfolioManager.Core.Dashboard;
using PortfolioManager.Core.Data;
using PortfolioManager.Core.Holdings;
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

/// <summary>
/// Family groups (Reports > Consolidated) and the figures across them. Groups belong to a login; callers pass the
/// portfolios that login can open, and only those are ever read.
/// </summary>
public sealed class ConsolidatedService(IDbConnectionFactory db, DashboardService dashboard, HoldingsService holdings,
    TaxableIncomeService income, CgtDiscountService cgt)
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
