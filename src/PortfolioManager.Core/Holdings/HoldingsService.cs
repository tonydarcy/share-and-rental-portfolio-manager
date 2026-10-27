using System.Data;
using Dapper;
using PortfolioManager.Core.Data;

namespace PortfolioManager.Core.Holdings;

public sealed record Holding(
    string StockCode,
    string ExchangeCode,
    decimal Units,
    decimal Price,
    decimal MarketValue,
    decimal CapitalGain,
    decimal Payouts,
    decimal TotalProfit,
    string Trend,
    decimal Cost = 0);

/// <summary>Cost is buys less sales and returns of capital, in portfolio currency.</summary>
public sealed record HoldingsTotal(decimal MarketValue, decimal CapitalGain, decimal Payouts, decimal TotalProfit, decimal Cost = 0);

public sealed record HoldingsSummary(IReadOnlyList<Holding> Holdings, HoldingsTotal Total);

public sealed record CustomGroup(int GroupId, string Name);

/// <summary>The holdings table from the legacy Portfolio page (GetProfitSummaryv2).</summary>
public sealed class HoldingsService(IDbConnectionFactory db)
{
    /// <summary>The portfolio's own custom groups (tblCustomGroups), for the group filter.</summary>
    public async Task<IReadOnlyList<CustomGroup>> GetGroupsAsync(int portfolioId)
    {
        await using var con = db.Create();
        var groups = await con.QueryAsync<CustomGroup>(
            "SELECT iGroupID AS GroupId, strGroupName AS Name FROM tblCustomGroups WHERE iUserID = @portfolioId ORDER BY strGroupName",
            new { portfolioId });
        return groups.ToList();
    }

    /// <summary>
    /// Holdings with market value and profit, in portfolio currency.
    /// groupId filters to one custom group; openOnly hides holdings that have been fully sold.
    /// </summary>
    public async Task<HoldingsSummary> GetHoldingsAsync(int portfolioId, int? groupId, bool openOnly)
    {
        await using var con = db.Create();
        // The proc's column names contain spaces, so read rows as dictionaries
        var rows = await con.QueryAsync("GetProfitSummaryv2",
            new { iUserID = portfolioId, iGroupID = groupId, bShowOpenPositionsOnly = openOnly, bShowTotalRows = true },
            commandType: CommandType.StoredProcedure);
        return Map(rows.Cast<IDictionary<string, object?>>());
    }

    /// <summary>
    /// Turns the proc's rows into holdings plus a total. The proc appends its total
    /// with a UNION (stock code NULL), which can come back in any position.
    /// </summary>
    public static HoldingsSummary Map(IEnumerable<IDictionary<string, object?>> rows)
    {
        var holdings = new List<Holding>();
        HoldingsTotal? total = null;

        foreach (var r in rows)
        {
            if (r["strStockCode"] is null or DBNull)
            {
                total = new HoldingsTotal(
                    Dec(r, "Market Value (Portfolio Currency)"),
                    Dec(r, "Total Capital Gain / Loss (Portfolio Currency)"),
                    Dec(r, "Total Payouts"),
                    Dec(r, "Total Profit / Loss (Portfolio Currency)"),
                    Cost(r));
                continue;
            }

            holdings.Add(new Holding(
                Convert.ToString(r["strStockCode"])!.Trim(),
                Convert.ToString(r["strStockExchangeCode"])?.Trim() ?? "",
                Dec(r, "Current Holding"),
                Dec(r, "Market Price (Portfolio Currency)"),
                Dec(r, "Market Value (Portfolio Currency)"),
                Dec(r, "Total Capital Gain / Loss (Portfolio Currency)"),
                Dec(r, "Total Payouts"),
                Dec(r, "Total Profit / Loss (Portfolio Currency)"),
                Convert.ToString(r["Trend Direction"]) ?? "-",
                Cost(r)));
        }

        holdings.Sort((a, b) => b.MarketValue.CompareTo(a.MarketValue));
        total ??= new HoldingsTotal(
            holdings.Sum(h => h.MarketValue), holdings.Sum(h => h.CapitalGain),
            holdings.Sum(h => h.Payouts), holdings.Sum(h => h.TotalProfit), holdings.Sum(h => h.Cost));
        return new HoldingsSummary(holdings, total);
    }

    /// <summary>
    /// The cost column comes from the 2026-10-27 migration. Before it runs, the proc's capital
    /// gain is market value less cost, so work it back from those.
    /// </summary>
    private static decimal Cost(IDictionary<string, object?> row) =>
        row.ContainsKey("Total Cost (Portfolio Currency)")
            ? Dec(row, "Total Cost (Portfolio Currency)")
            : Dec(row, "Market Value (Portfolio Currency)") - Dec(row, "Total Capital Gain / Loss (Portfolio Currency)");

    private static decimal Dec(IDictionary<string, object?> row, string column) =>
        row[column] is null or DBNull ? 0m : Convert.ToDecimal(row[column]);
}
