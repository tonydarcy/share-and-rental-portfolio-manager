using System.Data;
using Dapper;
using PortfolioManager.Core.Data;

namespace PortfolioManager.Core.Tax;

public sealed record TaxableIncomeSummary(
    decimal Unfranked,
    decimal Franked,
    decimal FrankingCredits,
    decimal Interest,
    decimal TrustIncome,
    decimal ForeignTaxPaid,
    decimal TaxWithheld,
    decimal GrossIncome);

public sealed record TaxableIncomeLine(
    string ExchangeCode,
    string StockCode,
    int PaymentCount,
    decimal CashReceived,
    decimal Unfranked,
    decimal Franked,
    decimal FrankingCredits,
    decimal Interest,
    decimal TrustIncome,
    decimal ForeignTaxPaid,
    decimal GrossIncome);

public sealed record TaxableIncomeReport(TaxableIncomeSummary Summary, IReadOnlyList<TaxableIncomeLine> Lines);

/// <summary>Dividends, distributions and interest for a tax return (uspGetTaxableIncomeReport).</summary>
public sealed class TaxableIncomeService(IDbConnectionFactory db)
{
    public async Task<TaxableIncomeReport> GetAsync(int portfolioId, DateOnly from, DateOnly to)
    {
        await using var con = db.Create();
        // Two result sets: one summary row, then a breakdown by holding
        await using var grid = await con.QueryMultipleAsync("uspGetTaxableIncomeReport",
            new { iUserId = portfolioId, dtStartDate = from.ToDateTime(TimeOnly.MinValue), dtEndDate = to.ToDateTime(TimeOnly.MinValue) },
            commandType: CommandType.StoredProcedure);
        var summary = (await grid.ReadAsync()).Cast<IDictionary<string, object?>>().FirstOrDefault();
        var lines = grid.IsConsumed ? [] : (await grid.ReadAsync()).Cast<IDictionary<string, object?>>().ToList();
        return Map(summary, lines);
    }

    /// <summary>
    /// SUM() returns NULL when there are no payouts in the period, so every NULL or missing
    /// amount shows as zero.
    /// </summary>
    public static TaxableIncomeReport Map(IDictionary<string, object?>? summary, IEnumerable<IDictionary<string, object?>> lines)
    {
        var s = summary ?? new Dictionary<string, object?>();
        return new TaxableIncomeReport(
            new TaxableIncomeSummary(
                Num.Dec(s, "TotalUnfranked"),
                Num.Dec(s, "TotalFranked"),
                Num.Dec(s, "TotalFrankingCredits"),
                Num.Dec(s, "TotalInterest"),
                Num.Dec(s, "TotalTrustIncome"),
                Num.Dec(s, "TotalForeignTaxPaid"),
                Num.Dec(s, "TaxWithheld"),
                Num.Dec(s, "GrossTaxableIncome")),
            lines.Select(r => new TaxableIncomeLine(
                Convert.ToString(r["Exchange"])?.Trim() ?? "",
                Convert.ToString(r["Code"])?.Trim() ?? "",
                r["PaymentCount"] is null or DBNull ? 0 : Convert.ToInt32(r["PaymentCount"]),
                Num.Dec(r, "CashReceived"),
                Num.Dec(r, "Unfranked"),
                Num.Dec(r, "Franked"),
                Num.Dec(r, "FrankingCredits"),
                Num.Dec(r, "Interest"),
                Num.Dec(r, "TrustIncome"),
                Num.Dec(r, "ForeignTaxPaid"),
                Num.Dec(r, "GrossIncome"))).ToList());
    }
}
