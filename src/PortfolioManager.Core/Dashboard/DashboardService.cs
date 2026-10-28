using System.Data;
using Dapper;
using PortfolioManager.Core.Data;

namespace PortfolioManager.Core.Dashboard;

public sealed class PortfolioValue
{
    public decimal TotalPortfolioValue { get; set; }
    public decimal TotalShareValue { get; set; }
    public decimal TotalCashValue { get; set; }
    public string? PortfolioCurrency { get; set; }
}

public sealed record ProjectedPayout(string StockExchangeCode, string StockCode, DateTime ProjectedPaymentDate, decimal ProjectedAmount);

public sealed class DashboardService(IDbConnectionFactory db)
{
    public async Task<PortfolioValue> GetValueAsync(int portfolioId)
    {
        await using var con = db.Create();
        var value = await con.QuerySingleOrDefaultAsync<PortfolioValue>("uspGetTotalPortfolioValue",
            new { iUserId = portfolioId }, commandType: CommandType.StoredProcedure);
        return value ?? new PortfolioValue();
    }

    /// <summary>The latest price date on file for each of the holdings, keyed by <see cref="DashboardFigures.PriceKey"/>.</summary>
    public async Task<IReadOnlyDictionary<string, DateTime>> GetLastPriceDatesAsync(IEnumerable<(string Exchange, string Code)> holdings)
    {
        var codes = holdings.Select(h => h.Code.Trim()).Where(c => c.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (codes.Length == 0)
            return new Dictionary<string, DateTime>();
        await using var con = db.Create();
        var rows = await con.QueryAsync<(string Exchange, string Code, DateTime Date)>("""
            SELECT strStockExchangeCode, strStockCode, MAX(dtmDetailsAsAtDate)
            FROM tblSharePrice
            WHERE strStockCode IN @codes AND dblClosePrice > 0
            GROUP BY strStockExchangeCode, strStockCode
            """, new { codes });
        var dates = new Dictionary<string, DateTime>();
        foreach (var r in rows)
        {
            var key = DashboardFigures.PriceKey(r.Exchange ?? "", r.Code ?? "");
            if (!dates.TryGetValue(key, out var d) || r.Date > d)
                dates[key] = r.Date;
        }
        return dates;
    }

    public async Task<IReadOnlyList<ProjectedPayout>> GetProjectedPayoutsAsync(int portfolioId)
    {
        await using var con = db.Create();
        var rows = await con.QueryAsync<ProjectedPayoutRow>(
            "uspGetProjectedPayouts", new { iUserId = portfolioId }, commandType: CommandType.StoredProcedure);
        return rows
            .Where(r => r.ProjectedPaymentDate is not null)
            .Select(r => new ProjectedPayout(r.strStockExchangeCode, r.strStockCode, r.ProjectedPaymentDate!.Value, r.ProjectedAmount ?? 0))
            .ToList();
    }

    /// <summary>Totals projected payouts by calendar month, in date order.</summary>
    public static IReadOnlyList<(DateTime Month, decimal Amount)> ByMonth(IEnumerable<ProjectedPayout> payouts) =>
        payouts
            .GroupBy(p => new DateTime(p.ProjectedPaymentDate.Year, p.ProjectedPaymentDate.Month, 1))
            .OrderBy(g => g.Key)
            .Select(g => (g.Key, g.Sum(p => p.ProjectedAmount)))
            .ToList();

    private sealed class ProjectedPayoutRow
    {
        public string strStockExchangeCode { get; set; } = "";
        public string strStockCode { get; set; } = "";
        public DateTime? ProjectedPaymentDate { get; set; }
        public decimal? ProjectedAmount { get; set; }
    }
}
