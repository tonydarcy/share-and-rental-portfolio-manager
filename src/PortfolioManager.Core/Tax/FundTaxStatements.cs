using System.Data;
using Dapper;
using PortfolioManager.Core.Data;

namespace PortfolioManager.Core.Tax;

/// <summary>
/// A fund's annual tax statement (an AMMA statement for an attribution MIT, or a standard distribution statement),
/// keyed in once a year. Year is the financial year it ends (2026 for 2025-26).
/// </summary>
public sealed record FundTaxStatement(
    int Id, string ExchangeCode, string StockCode, int Year, DateOnly YearEnd,
    decimal CashDistribution, decimal Franked, decimal FrankingCredits, decimal Unfranked, decimal Interest,
    decimal OtherIncome, decimal Ncmi, decimal DiscountedGain, decimal OtherGain, decimal ForeignIncome,
    decimal ForeignTaxCredits, decimal TfnWithheld, decimal CostBaseIncrease, decimal CostBaseDecrease, string? Comment)
{
    /// <summary>The fund applied the 50% discount to these gains, so the gross gain is twice the discounted amount.</summary>
    public decimal GrossDiscountGain => DiscountedGain * 2;

    /// <summary>Positive reduces the cost base (AMIT net decrease, or tax-deferred amounts); negative increases it.</summary>
    public decimal NetCostBaseDecrease => CostBaseDecrease - CostBaseIncrease;

    /// <summary>Assessable income other than capital gains (foreign income is entered gross, including the foreign tax).</summary>
    public decimal AssessableIncome => Franked + FrankingCredits + Unfranked + Interest + OtherIncome + Ncmi + ForeignIncome;

    /// <summary>
    /// How far the statement is from the cash paid: the components (foreign income less its tax, discount gains grossed up
    /// as the fund's CGT concession amount is paid out too), plus an AMIT decrease or less an increase, less TFN amounts
    /// withheld, should equal the cash distribution. Zero when it ties.
    /// </summary>
    public decimal Difference =>
        Franked + Unfranked + Interest + OtherIncome + Ncmi + (ForeignIncome - ForeignTaxCredits) + GrossDiscountGain + OtherGain
        + CostBaseDecrease - CostBaseIncrease - TfnWithheld - CashDistribution;

    /// <summary>For the CGT engines: the cost base change and gains, at the year end.</summary>
    public CapitalReturn ToCostBaseLot() =>
        new(ExchangeCode, StockCode, YearEnd, NetCostBaseDecrease, GrossDiscountGain, OtherGain, FromStatement: true);
}

public static class FundTaxStatements
{
    /// <summary>
    /// Validates the amounts typed in. The CGT discount line on a statement (the fund's 50%) isn't entered: the discounted
    /// gain is grossed up from it.
    /// </summary>
    public static IReadOnlyList<string> Validate(FundTaxStatement s)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(s.StockCode))
            errors.Add("Pick the fund.");
        if (s.Year < 2000 || s.Year > 2100)
            errors.Add("Pick the financial year.");
        if (new[] { s.CashDistribution, s.Franked, s.FrankingCredits, s.Unfranked, s.Interest, s.OtherIncome, s.Ncmi, s.DiscountedGain, s.OtherGain,
                s.ForeignIncome, s.ForeignTaxCredits, s.TfnWithheld, s.CostBaseIncrease, s.CostBaseDecrease }.Any(v => v < 0))
            errors.Add("Amounts can't be negative.");
        if (s.CostBaseIncrease > 0 && s.CostBaseDecrease > 0)
            errors.Add("Enter the net AMIT amount: either an increase or a decrease, not both.");
        return errors;
    }

    /// <summary>The year's income figures: payouts for funds without a statement, and the statements' figures for funds with one.</summary>
    public static (IReadOnlyList<Payouts.Payout> Payouts, IReadOnlyList<FundTaxStatement> Statements) Split(
        IEnumerable<Payouts.Payout> payouts, IEnumerable<FundTaxStatement> statements, int year)
    {
        var those = statements.Where(s => s.Year == year).ToList();
        var codes = those.Select(s => (s.ExchangeCode.Trim().ToUpperInvariant(), s.StockCode.Trim().ToUpperInvariant())).ToHashSet();
        return (payouts.Where(p => !codes.Contains(((p.StockExchangeCode ?? "").Trim().ToUpperInvariant(), (p.StockCode ?? "").Trim().ToUpperInvariant()))).ToList(), those);
    }
}

/// <summary>tblFundTaxStatements, written through the procs in 2026-10-21-07-fund-tax-statements.sql.</summary>
public sealed class FundTaxStatementService(IDbConnectionFactory db)
{
    private const string Columns = """
        iFundTaxStatementID AS Id, strStockExchangeCode AS ExchangeCode, strStockCode AS StockCode, iFinancialYear AS Year,
        CAST(dtmYearEnd AS datetime) AS YearEnd, dblCashDistribution AS CashDistribution, dblFranked AS Franked,
        dblFrankingCredits AS FrankingCredits, dblUnfranked AS Unfranked, dblInterest AS Interest, dblOtherIncome AS OtherIncome,
        dblNcmi AS Ncmi, dblDiscountedGain AS DiscountedGain, dblOtherGain AS OtherGain, dblForeignIncome AS ForeignIncome,
        dblForeignTaxCredits AS ForeignTaxCredits, dblTfnWithheld AS TfnWithheld, dblCostBaseIncrease AS CostBaseIncrease,
        dblCostBaseDecrease AS CostBaseDecrease, strComment AS Comment
        """;

    private sealed record Row(int Id, string ExchangeCode, string StockCode, int Year, DateTime YearEnd, double CashDistribution, double Franked,
        double FrankingCredits, double Unfranked, double Interest, double OtherIncome, double Ncmi, double DiscountedGain, double OtherGain,
        double ForeignIncome, double ForeignTaxCredits, double TfnWithheld, double CostBaseIncrease, double CostBaseDecrease, string? Comment)
    {
        public FundTaxStatement ToStatement() => new(Id, ExchangeCode.Trim(), StockCode.Trim(), Year, DateOnly.FromDateTime(YearEnd),
            M(CashDistribution), M(Franked), M(FrankingCredits), M(Unfranked), M(Interest), M(OtherIncome), M(Ncmi), M(DiscountedGain),
            M(OtherGain), M(ForeignIncome), M(ForeignTaxCredits), M(TfnWithheld), M(CostBaseIncrease), M(CostBaseDecrease), Comment);

        private static decimal M(double d) => Math.Round((decimal)d, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>The portfolio's statements, newest year first. Empty when the migration hasn't been run.</summary>
    public async Task<IReadOnlyList<FundTaxStatement>> GetAsync(int portfolioId)
    {
        await using var con = db.Create();
        try
        {
            var rows = await con.QueryAsync<Row>($"SELECT {Columns} FROM tblFundTaxStatements WHERE iUserID = @portfolioId ORDER BY iFinancialYear DESC, strStockCode",
                new { portfolioId });
            return rows.Select(r => r.ToStatement()).ToList();
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208)
        {
            return [];
        }
    }

    public async Task<ProcResult> SaveAsync(int portfolioId, FundTaxStatement s)
    {
        await using var con = db.Create();
        var row = await con.QuerySingleAsync<(string Result, int? Id)>("uspSaveFundTaxStatement", new
        {
            iUserID = portfolioId, iFundTaxStatementID = s.Id, strStockExchangeCode = s.ExchangeCode, strStockCode = s.StockCode,
            iFinancialYear = s.Year, dtmYearEnd = s.YearEnd.ToDateTime(TimeOnly.MinValue),
            dblCashDistribution = (double)s.CashDistribution, dblFranked = (double)s.Franked, dblFrankingCredits = (double)s.FrankingCredits,
            dblUnfranked = (double)s.Unfranked, dblInterest = (double)s.Interest, dblOtherIncome = (double)s.OtherIncome, dblNcmi = (double)s.Ncmi,
            dblDiscountedGain = (double)s.DiscountedGain, dblOtherGain = (double)s.OtherGain, dblForeignIncome = (double)s.ForeignIncome,
            dblForeignTaxCredits = (double)s.ForeignTaxCredits, dblTfnWithheld = (double)s.TfnWithheld,
            dblCostBaseIncrease = (double)s.CostBaseIncrease, dblCostBaseDecrease = (double)s.CostBaseDecrease,
            strComment = string.IsNullOrWhiteSpace(s.Comment) ? null : s.Comment.Trim(),
        }, commandType: CommandType.StoredProcedure);
        return ProcResult.From(row.Result, "Success", row.Id);
    }

    public async Task<ProcResult> DeleteAsync(int portfolioId, int id)
    {
        await using var con = db.Create();
        var result = await con.ExecuteScalarAsync<string>("uspDeleteFundTaxStatement", new { iUserID = portfolioId, iFundTaxStatementID = id },
            commandType: CommandType.StoredProcedure);
        return ProcResult.From(result, "Success");
    }
}
