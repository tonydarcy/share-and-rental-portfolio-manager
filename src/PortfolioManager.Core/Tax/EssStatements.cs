using System.Data;
using Dapper;
using PortfolioManager.Core.Data;

namespace PortfolioManager.Core.Tax;

/// <summary>
/// An employer's employee share scheme (ESS) statement for a year (tblEssStatements). The discounts are what an
/// individual reports at item 12: taxed upfront and eligible for the $1,000 reduction (D), taxed upfront and not
/// eligible (E), deferral schemes (F), and interests acquired before 1 July 2009 whose cessation time was this year (G).
/// ForeignSource (A) is the part of those that's from foreign employment; TfnWithheld is C.
/// </summary>
public sealed record EssStatement(
    int Id, int Year, string Employer, decimal UpfrontEligible, decimal UpfrontNotEligible, decimal Deferral, decimal Pre2009,
    decimal ForeignSource, decimal TfnWithheld, string? Comment = null)
{
    public decimal TotalDiscount => UpfrontEligible + UpfrontNotEligible + Deferral + Pre2009;
}

/// <summary>A year's ESS statements added together, for the return labels.</summary>
public sealed record EssYear(decimal UpfrontEligible, decimal UpfrontNotEligible, decimal Deferral, decimal Pre2009, decimal ForeignSource, decimal TfnWithheld)
{
    public static readonly EssYear None = new(0, 0, 0, 0, 0, 0);

    public decimal TotalDiscount => UpfrontEligible + UpfrontNotEligible + Deferral + Pre2009;

    public static EssYear For(IEnumerable<EssStatement> statements, int year)
    {
        var those = statements.Where(s => s.Year == year).ToList();
        return new(those.Sum(s => s.UpfrontEligible), those.Sum(s => s.UpfrontNotEligible), those.Sum(s => s.Deferral),
            those.Sum(s => s.Pre2009), those.Sum(s => s.ForeignSource), those.Sum(s => s.TfnWithheld));
    }
}

public static class EssStatements
{
    public static IReadOnlyList<string> Validate(EssStatement s)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(s.Employer))
            errors.Add("Enter the employer.");
        else if (s.Employer.Trim().Length > 200)
            errors.Add("The employer's name is too long.");
        decimal[] amounts = [s.UpfrontEligible, s.UpfrontNotEligible, s.Deferral, s.Pre2009, s.ForeignSource, s.TfnWithheld];
        if (amounts.Any(a => a < 0 || a > Validation.FormRules.MaxMoney))
            errors.Add("Amounts can't be negative.");
        if (s.TotalDiscount == 0 && s.TfnWithheld == 0)
            errors.Add("Enter at least one discount from the statement.");
        if (s.ForeignSource > s.TotalDiscount)
            errors.Add("The foreign source discount is part of the discounts, so it can't be more than their total.");
        return errors;
    }
}

/// <summary>tblEssStatements, written through the procs in 2026-10-21-10-employee-shares-options.sql.</summary>
public sealed class EssStatementService(IDbConnectionFactory db)
{
    private sealed record Row(int Id, int Year, string Employer, double UpfrontEligible, double UpfrontNotEligible, double Deferral,
        double Pre2009, double ForeignSource, double TfnWithheld, string? Comment)
    {
        public EssStatement ToStatement() => new(Id, Year, Employer, M(UpfrontEligible), M(UpfrontNotEligible), M(Deferral), M(Pre2009),
            M(ForeignSource), M(TfnWithheld), Comment);

        private static decimal M(double d) => Math.Round((decimal)d, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>The portfolio's statements, newest year first. Empty when the migration hasn't been run.</summary>
    public async Task<IReadOnlyList<EssStatement>> GetAsync(int portfolioId)
    {
        await using var con = db.Create();
        try
        {
            var rows = await con.QueryAsync<Row>("""
                SELECT iEssStatementID AS Id, iFinancialYear AS Year, strEmployer AS Employer, dblUpfrontEligible AS UpfrontEligible,
                       dblUpfrontNotEligible AS UpfrontNotEligible, dblDeferral AS Deferral, dblPre2009 AS Pre2009,
                       dblForeignSource AS ForeignSource, dblTfnWithheld AS TfnWithheld, strComment AS Comment
                FROM tblEssStatements WHERE iUserID = @portfolioId ORDER BY iFinancialYear DESC, strEmployer
                """, new { portfolioId });
            return rows.Select(r => r.ToStatement()).ToList();
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208)
        {
            return [];
        }
    }

    public async Task<ProcResult> SaveAsync(int portfolioId, EssStatement s)
    {
        await using var con = db.Create();
        var row = await con.QuerySingleAsync<(string Result, int? Id)>("uspSaveEssStatement", new
        {
            iUserID = portfolioId, iEssStatementID = s.Id, iFinancialYear = s.Year,
            dtmYearEnd = FinancialYear.EndDate(s.Year).ToDateTime(TimeOnly.MinValue), strEmployer = s.Employer.Trim(),
            dblUpfrontEligible = (double)s.UpfrontEligible, dblUpfrontNotEligible = (double)s.UpfrontNotEligible,
            dblDeferral = (double)s.Deferral, dblPre2009 = (double)s.Pre2009, dblForeignSource = (double)s.ForeignSource,
            dblTfnWithheld = (double)s.TfnWithheld, strComment = string.IsNullOrWhiteSpace(s.Comment) ? null : s.Comment.Trim(),
        }, commandType: CommandType.StoredProcedure);
        return ProcResult.From(row.Result, "Success", row.Id);
    }

    public async Task<ProcResult> DeleteAsync(int portfolioId, int id)
    {
        await using var con = db.Create();
        var result = await con.ExecuteScalarAsync<string>("uspDeleteEssStatement", new { iUserID = portfolioId, iEssStatementID = id },
            commandType: CommandType.StoredProcedure);
        return ProcResult.From(result, "Success");
    }
}
