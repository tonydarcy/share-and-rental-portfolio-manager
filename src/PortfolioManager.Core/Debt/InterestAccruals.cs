using System.Data;
using Dapper;
using PortfolioManager.Core.Data;
using PortfolioManager.Core.Tax;

namespace PortfolioManager.Core.Debt;

/// <summary>One line of a year-end accrual: a holding's unpaid interest, or a loan's interest not yet charged.</summary>
public sealed record AccrualLine(string Name, decimal Amount, string Note);

/// <summary>The interest accruals at a year end: earned on term deposits and bonds but not yet paid, and owed on loans but not yet charged.</summary>
public sealed record InterestAccruals(int Year, DateOnly YearEnd, IReadOnlyList<AccrualLine> Receivable, IReadOnlyList<AccrualLine> Payable)
{
    public decimal ReceivableTotal => Receivable.Sum(l => l.Amount);
    public decimal PayableTotal => Payable.Sum(l => l.Amount);

    public static InterestAccruals Build(int year, IEnumerable<FixedIncomeHolding> holdings, IEnumerable<Loan> loans, IEnumerable<LoanEntry> entries)
    {
        var end = FinancialYear.EndDate(year);
        var entryList = entries.ToList();
        var receivable = holdings.Select(h => (h, y: FixedIncomeCalc.Year(h, FinancialYear.StartDate(year), end)))
            .Where(x => x.y.AccruedAtEnd > 0)
            .Select(x => new AccrualLine(x.h.Name, x.y.AccruedAtEnd,
                $"Since {(x.y.LastPayment ?? x.h.Start):d MMM yyyy}" + (x.y.NextPayment is { } n ? $", paid {n:d MMM yyyy}" : "")))
            .ToList();
        var payable = loans.Select(l => new AccrualLine(l.Name, LoanCalc.AccruedInterest(l, entryList, end), $"At {l.RatePercent:0.##}% since interest was last charged"))
            .Where(l => l.Amount > 0)
            .ToList();
        return new InterestAccruals(year, end, receivable, payable);
    }

    public object Parameters(int portfolioId) => new
    {
        iUserID = portfolioId, iYear = Year, dtmYearEnd = YearEnd.ToDateTime(TimeOnly.MinValue), dblReceivable = ReceivableTotal, dblPayable = PayableTotal,
    };
}

/// <summary>Works out and posts the year-end interest accruals (uspPostInterestAccruals, 2026-10-26-07-loans-in-ledger.sql).</summary>
public sealed class InterestAccrualService(IDbConnectionFactory db, FixedIncomeService fixedIncome, LoanService loans)
{
    public async Task<InterestAccruals> GetAsync(int portfolioId, int year)
    {
        var (all, entries) = await loans.GetAsync(portfolioId);
        return InterestAccruals.Build(year, await fixedIncome.GetAsync(portfolioId), all, entries);
    }

    public async Task<ProcResult> PostAsync(int portfolioId, int year)
    {
        var accruals = await GetAsync(portfolioId, year);
        await using var con = db.Create();
        if (await con.ExecuteScalarAsync<int>("SELECT CASE WHEN OBJECT_ID('dbo.uspPostInterestAccruals', 'P') IS NULL THEN 0 ELSE 1 END") == 0)
            return ProcResult.Failed("Posting accruals needs the database update 2026-10-26-07-loans-in-ledger.sql.");
        return ProcResult.From(await con.ExecuteScalarAsync<string>("uspPostInterestAccruals", accruals.Parameters(portfolioId),
            commandType: CommandType.StoredProcedure), "Success");
    }
}
