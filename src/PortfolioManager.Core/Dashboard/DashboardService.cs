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

public sealed class HistoryPoint
{
    public DateTime Date { get; set; }
    public decimal TotalEquity { get; set; }
}

public sealed record ProjectedPayout(string StockExchangeCode, string StockCode, DateTime ProjectedPaymentDate, decimal ProjectedAmount);

public sealed class DashboardService(IDbConnectionFactory db)
{
    public const int HistoryDays = 90;

    public async Task<PortfolioValue> GetValueAsync(int portfolioId)
    {
        await using var con = db.Create();
        var value = await con.QuerySingleOrDefaultAsync<PortfolioValue>("uspGetTotalPortfolioValue",
            new { iUserId = portfolioId }, commandType: CommandType.StoredProcedure);
        return value ?? new PortfolioValue();
    }

    /// <summary>
    /// Daily total equity for the last 90 days. Like the legacy Dashboard, it first
    /// recalculates those days with uspBackfillHistory so back-dated trades show up.
    /// </summary>
    public async Task<IReadOnlyList<HistoryPoint>> GetHistoryAsync(int portfolioId, DateTime today)
    {
        var start = today.Date.AddDays(-HistoryDays);
        await using var con = db.Create();
        await con.ExecuteAsync("uspBackfillHistory",
            new { iUserId = portfolioId, dtStartDate = start, dtEndDate = today.Date },
            commandType: CommandType.StoredProcedure, commandTimeout: 120);

        var points = await con.QueryAsync<HistoryPoint>(
            "SELECT dtDate AS Date, dblTotalEquity AS TotalEquity FROM tblDailyPortfolioHistory " +
            "WHERE iUserID = @portfolioId AND dtDate >= @start ORDER BY dtDate",
            new { portfolioId, start });
        return points.ToList();
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
