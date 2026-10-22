using Dapper;
using PortfolioManager.Core.CorporateActions;
using PortfolioManager.Core.Data;
using PortfolioManager.Core.Tax;

namespace PortfolioManager.Core.Reports;

/// <summary>A dividend announced for a holding that hasn't been paid yet (or whose ex-date is still to come).</summary>
public sealed record UpcomingIncome(
    string ExchangeCode, string StockCode, DateOnly ExDate, DateOnly? PaymentDate, decimal AmountPerUnit, string? Currency,
    decimal Units, bool ExDatePassed)
{
    public decimal Estimate => Math.Round(Units * AmountPerUnit, 2, MidpointRounding.AwayFromZero);
}

/// <summary>Income received from one holding in one month.</summary>
public sealed record MonthlyIncome(string ExchangeCode, string StockCode, int Year, int Month, decimal Cash, decimal FrankingCredits);

/// <summary>Income received in a period, by holding and month, with the months in order (a financial year's 12).</summary>
public sealed record IncomeByMonth(IReadOnlyList<(int Year, int Month)> Months, IReadOnlyList<MonthlyIncome> Rows)
{
    public IEnumerable<(string ExchangeCode, string StockCode)> Holdings =>
        Rows.Select(r => (r.ExchangeCode, r.StockCode)).Distinct().OrderBy(h => h.StockCode, StringComparer.OrdinalIgnoreCase);

    public decimal Cash(string exchange, string code, int year, int month) =>
        Rows.Where(r => r.ExchangeCode == exchange && r.StockCode == code && r.Year == year && r.Month == month).Sum(r => r.Cash);

    public decimal MonthTotal(int year, int month) => Rows.Where(r => r.Year == year && r.Month == month).Sum(r => r.Cash);

    public decimal HoldingTotal(string exchange, string code) => Rows.Where(r => r.ExchangeCode == exchange && r.StockCode == code).Sum(r => r.Cash);

    public decimal Total => Rows.Sum(r => r.Cash);

    public decimal FrankingCredits => Rows.Sum(r => r.FrankingCredits);

    /// <summary>The months from the start of the period to its end, so empty months still show.</summary>
    public static IReadOnlyList<(int Year, int Month)> MonthsOf(DateOnly from, DateOnly to)
    {
        var months = new List<(int, int)>();
        for (var d = new DateOnly(from.Year, from.Month, 1); d <= to && months.Count < 120; d = d.AddMonths(1))
            months.Add((d.Year, d.Month));
        return months;
    }
}

/// <summary>Upcoming dividends (from the announcements the price updater saves) and income received by month (tblPayouts).</summary>
public sealed class IncomeCalendarService(IDbConnectionFactory db)
{
    private sealed record UpcomingRow(string ExchangeCode, string StockCode, DateTime ExDate, DateTime? PaymentDate, double AmountPerUnit,
        string? Currency, double? Units, double? LaterFactor);

    /// <summary>
    /// Announced dividends not yet paid, on units held at the ex-date (today's holding when the ex-date is still to come),
    /// leaving out ones already recorded as a payout. Empty until the announcements migration is run.
    /// </summary>
    public async Task<IReadOnlyList<UpcomingIncome>> GetUpcomingAsync(int portfolioId, DateOnly today)
    {
        await using var con = db.Create();
        IEnumerable<UpcomingRow> rows;
        try
        {
            rows = await con.QueryAsync<UpcomingRow>("""
                SELECT D.strStockExchangeCode AS ExchangeCode, D.strStockCode AS StockCode,
                       CAST(D.dtmExDate AS datetime) AS ExDate, CAST(D.dtmPaymentDate AS datetime) AS PaymentDate,
                       D.dblAmountPerUnit AS AmountPerUnit, D.strCurrency AS Currency,
                       (SELECT SUM(CASE WHEN UPPER(T.strType) = 'BUY' THEN T.iQuantity WHEN UPPER(T.strType) = 'SELL' THEN -T.iQuantity ELSE 0 END)
                          FROM tblShareTrades T
                         WHERE T.iUserId = @portfolioId AND T.strStockExchangeCode = D.strStockExchangeCode
                           AND T.strStockCode = D.strStockCode AND T.dtmDateOfTransaction < D.dtmExDate) AS Units,
                       (SELECT EXP(SUM(LOG(A.dblRatio))) FROM tblCorporateActions A
                         WHERE A.iUserID = @portfolioId AND A.strStockExchangeCode = D.strStockExchangeCode AND A.strStockCode = D.strStockCode
                           AND A.strActionType IN ('SPLIT', 'CONSOLIDATION') AND A.dblRatio > 0 AND A.dtmActionDate > D.dtmExDate) AS LaterFactor
                FROM tblAnnouncedDividends D
                WHERE ISNULL(D.dtmPaymentDate, D.dtmExDate) >= @today AND D.dtmExDate >= DATEADD(day, -120, @today)
                  AND EXISTS (SELECT 1 FROM tblShareTrades T WHERE T.iUserId = @portfolioId
                                AND T.strStockExchangeCode = D.strStockExchangeCode AND T.strStockCode = D.strStockCode)
                  AND NOT EXISTS (SELECT 1 FROM tblPayouts P WHERE P.iUserID = @portfolioId
                                    AND P.strStockExchangeCode = D.strStockExchangeCode AND P.strStockCode = D.strStockCode
                                    AND P.dtmTransactionDate >= DATEADD(day, -3, D.dtmExDate)
                                    AND P.dtmTransactionDate <= DATEADD(day, 45, ISNULL(D.dtmPaymentDate, D.dtmExDate)))
                ORDER BY ISNULL(D.dtmPaymentDate, D.dtmExDate), D.strStockCode
                """, new { portfolioId, today = today.ToDateTime(TimeOnly.MinValue) });
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208)
        {
            return []; // tblAnnouncedDividends isn't there yet
        }

        return rows
            .Select(r => new UpcomingIncome(r.ExchangeCode, r.StockCode, DateOnly.FromDateTime(r.ExDate),
                r.PaymentDate is { } p ? DateOnly.FromDateTime(p) : null, (decimal)r.AmountPerUnit, r.Currency,
                AnnouncedPayouts.UnitsOnTheDay((decimal)(r.Units ?? 0), (decimal)(r.LaterFactor ?? 1)),
                DateOnly.FromDateTime(r.ExDate) <= today))
            .Where(u => u.Units > 0.0001m)
            .ToList();
    }

    public async Task<IncomeByMonth> GetByMonthAsync(int portfolioId, ReportPeriod period)
    {
        await using var con = db.Create();
        var rows = await con.QueryAsync<(string Exchange, string Code, int Year, int Month, double? Cash, double? Credits)>("""
            SELECT ISNULL(strStockExchangeCode, ''), ISNULL(strStockCode, ''), YEAR(dtmTransactionDate), MONTH(dtmTransactionDate),
                   SUM(ISNULL(dblTotal, 0)), SUM(ISNULL(dblFrankingCredit, 0))
            FROM tblPayouts
            WHERE iUserID = @portfolioId AND dtmTransactionDate >= @from AND dtmTransactionDate < DATEADD(day, 1, @to)
            GROUP BY ISNULL(strStockExchangeCode, ''), ISNULL(strStockCode, ''), YEAR(dtmTransactionDate), MONTH(dtmTransactionDate)
            """, new { portfolioId, from = period.From.ToDateTime(TimeOnly.MinValue), to = period.To.ToDateTime(TimeOnly.MinValue) });
        return new IncomeByMonth(IncomeByMonth.MonthsOf(period.From, period.To),
            rows.Select(r => new MonthlyIncome(r.Exchange.Trim().ToUpperInvariant(), r.Code.Trim().ToUpperInvariant(), r.Year, r.Month,
                (decimal)(r.Cash ?? 0), (decimal)(r.Credits ?? 0))).ToList());
    }
}
