using System.Data;
using System.Globalization;
using System.Text;
using Dapper;
using PortfolioManager.Core.Data;

namespace PortfolioManager.Core.Tax;

/// <summary>
/// The $1,000 reduction (s83A-35 ITAA 1997) on a discount from a taxed-upfront scheme that's eligible for it: up to $1,000
/// comes off when the income test amount (taxable income worked out without the reduction, plus reportable fringe benefits,
/// reportable super contributions and total net investment loss) is $180,000 or less.
/// </summary>
public static class EssReduction
{
    public const decimal Cap = 1_000m;
    public const decimal IncomeTestLimit = 180_000m;

    public static decimal IncomeTest(decimal taxableIncomeBeforeReduction, decimal reportableFringeBenefits, decimal reportableSuper, decimal netInvestmentLoss) =>
        Math.Max(0, taxableIncomeBeforeReduction) + Math.Max(0, reportableFringeBenefits) + Math.Max(0, reportableSuper) + Math.Max(0, netInvestmentLoss);

    public static decimal For(decimal upfrontEligible, decimal incomeTest) =>
        incomeTest <= IncomeTestLimit ? Math.Min(Cap, Math.Max(0, upfrontEligible)) : 0;
}

/// <summary>The kinds of scheme an employer reports, and the label each goes to on the employee's return.</summary>
public static class EssSchemes
{
    public const string UpfrontEligible = "UpfrontEligible", UpfrontNotEligible = "UpfrontNotEligible", Deferral = "Deferral", StartUp = "StartUp";
    public static readonly string[] All = [UpfrontEligible, UpfrontNotEligible, Deferral, StartUp];
    public static readonly string[] Interests = ["Shares", "Rights", "Options"];

    public static string Label(string scheme) => scheme switch
    {
        UpfrontEligible => "Taxed upfront, eligible for the $1,000 reduction",
        UpfrontNotEligible => "Taxed upfront, not eligible for the reduction",
        Deferral => "Tax-deferred scheme",
        StartUp => "Start-up concession",
        _ => scheme,
    };

    /// <summary>Where the employee puts the discount on an individual return (item 12), or null when it isn't taxed now.</summary>
    public static string? ReturnLabel(string scheme) => scheme switch
    {
        UpfrontEligible => "12D",
        UpfrontNotEligible => "12E",
        Deferral => "12F",
        _ => null,
    };

    /// <summary>
    /// The rate an employer withholds from the discount when the employee hasn't quoted a TFN or ABN: the top marginal
    /// rate plus the Medicare levy.
    /// </summary>
    public const decimal NoTfnRate = 0.47m;
}

/// <summary>One grant of ESS interests to an employee (tblEssGrants). Market value and amount paid are totals.</summary>
public sealed record EssGrant(
    int Id, string Employee, string? EmployeeRef, string Scheme, string Interest, DateOnly Acquired, decimal Interests,
    decimal MarketValue, decimal AmountPaid, decimal? ExercisePrice, DateOnly? TaxingPoint, decimal? TaxingPointValue,
    bool TfnQuoted, decimal TfnWithheld, bool ForeignSource, string? Comment)
{
    /// <summary>The date that puts the grant in a year's report: the deferred taxing point for a deferral scheme, otherwise when acquired.</summary>
    public DateOnly? ReportDate => Scheme == EssSchemes.Deferral ? TaxingPoint : Acquired;

    /// <summary>The discount: market value less what was paid, at acquisition, or at the deferred taxing point for a deferral scheme.</summary>
    public decimal Discount => Math.Max(0, Math.Round((Scheme == EssSchemes.Deferral ? TaxingPointValue ?? 0 : MarketValue) - AmountPaid, 2));

    /// <summary>What should have been withheld when no TFN was quoted (the start-up concession has nothing to withhold).</summary>
    public decimal WithholdingDue => TfnQuoted || Scheme == EssSchemes.StartUp ? 0 : Math.Round(Discount * EssSchemes.NoTfnRate, 2);
}

/// <summary>One employee's year, as their ESS statement sets it out.</summary>
public sealed record EssEmployeeYear(string Employee, string? EmployeeRef, IReadOnlyList<EssGrant> Grants)
{
    public decimal UpfrontEligible => Sum(EssSchemes.UpfrontEligible);
    public decimal UpfrontNotEligible => Sum(EssSchemes.UpfrontNotEligible);
    public decimal Deferral => Sum(EssSchemes.Deferral);
    public decimal StartUp => Sum(EssSchemes.StartUp);
    public decimal ForeignSource => Grants.Where(g => g.ForeignSource && g.Scheme != EssSchemes.StartUp).Sum(g => g.Discount);
    public decimal TfnWithheld => Grants.Sum(g => g.TfnWithheld);
    public bool AnyWithholdingShort => Grants.Any(g => g.TfnWithheld < g.WithholdingDue);

    private decimal Sum(string scheme) => Grants.Where(g => g.Scheme == scheme).Sum(g => g.Discount);
}

/// <summary>The employer's register for a year: each employee's grants reported in it, and those still waiting for a deferred taxing point.</summary>
public sealed record EssRegister(int Year, IReadOnlyList<EssEmployeeYear> Employees, IReadOnlyList<EssGrant> AwaitingTaxingPoint)
{
    public decimal TotalDiscount => Employees.Sum(e => e.Grants.Sum(g => g.Discount));
}

public static class EssRegisters
{
    public static EssRegister Build(IEnumerable<EssGrant> grants, int year, Regions.YearStart? start = null)
    {
        var list = grants.ToList();
        var employees = list.Where(g => g.ReportDate is { } d && FinancialYear.EndYearFor(d, start) == year)
            .GroupBy(g => (g.Employee.Trim().ToUpperInvariant(), (g.EmployeeRef ?? "").Trim().ToUpperInvariant()))
            .Select(g => new EssEmployeeYear(g.First().Employee.Trim(), g.First().EmployeeRef, g.OrderBy(x => x.Acquired).ToList()))
            .OrderBy(e => e.Employee, StringComparer.OrdinalIgnoreCase).ToList();
        var waiting = list.Where(g => g.Scheme == EssSchemes.Deferral && g.TaxingPoint is null && FinancialYear.EndYearFor(g.Acquired, start) <= year)
            .OrderBy(g => g.Acquired).ToList();
        return new EssRegister(year, employees, waiting);
    }

    public static IReadOnlyList<string> Validate(EssGrant g)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(g.Employee))
            errors.Add("Enter the employee.");
        else if (g.Employee.Trim().Length > 200)
            errors.Add("The employee's name is too long.");
        if ((g.EmployeeRef?.Trim().Length ?? 0) > 50)
            errors.Add("The employee reference is too long.");
        if (g.EmployeeRef is { } r && r.Count(char.IsDigit) is 8 or 9 && r.All(c => char.IsDigit(c) || c == ' '))
            errors.Add("The reference looks like a tax file number. Don't keep TFNs here: use a payroll number or leave it blank.");
        if (!EssSchemes.All.Contains(g.Scheme))
            errors.Add("Pick the kind of scheme.");
        if (!EssSchemes.Interests.Contains(g.Interest))
            errors.Add("Pick shares, rights or options.");
        if (g.Interests <= 0)
            errors.Add("Enter how many were granted.");
        if (new[] { g.MarketValue, g.AmountPaid, g.TfnWithheld, g.TaxingPointValue ?? 0, g.ExercisePrice ?? 0 }.Any(v => v < 0 || v > Validation.FormRules.MaxMoney))
            errors.Add("Amounts can't be negative.");
        if (g.Scheme != EssSchemes.Deferral && (g.TaxingPoint is not null || g.TaxingPointValue is not null))
            errors.Add("Only a deferral scheme has a deferred taxing point.");
        if (g.TaxingPoint is { } t && t < g.Acquired)
            errors.Add("The deferred taxing point can't be before the grant.");
        if (g.TaxingPoint is not null && g.TaxingPointValue is null)
            errors.Add("Enter the market value at the deferred taxing point.");
        return errors;
    }

    /// <summary>
    /// The register as a CSV, one line per grant, with the facts the ATO's ESS annual report asks for (other than the
    /// employee's TFN, address and date of birth, which this site doesn't keep).
    /// </summary>
    public static string ToCsv(EssRegister register)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Financial year,Employee,Employee reference,Scheme,Return label,Interest,Date acquired,Number,Market value,Amount paid," +
            "Exercise price,Deferred taxing point,Market value at taxing point,Discount,Foreign source,TFN quoted,TFN amount withheld");
        static string Q(string? s) => s is null ? "" : s.Contains(',') || s.Contains('"') ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
        static string N(decimal? d) => d?.ToString("0.########", CultureInfo.InvariantCulture) ?? "";
        foreach (var e in register.Employees)
            foreach (var g in e.Grants)
                sb.Append(FinancialYear.Label(register.Year)).Append(',').Append(Q(e.Employee)).Append(',').Append(Q(e.EmployeeRef)).Append(',')
                    .Append(Q(EssSchemes.Label(g.Scheme))).Append(',').Append(EssSchemes.ReturnLabel(g.Scheme)).Append(',').Append(g.Interest).Append(',')
                    .Append(g.Acquired.ToString("yyyy-MM-dd")).Append(',').Append(N(g.Interests)).Append(',').Append(N(g.MarketValue)).Append(',')
                    .Append(N(g.AmountPaid)).Append(',').Append(N(g.ExercisePrice)).Append(',').Append(g.TaxingPoint?.ToString("yyyy-MM-dd")).Append(',')
                    .Append(N(g.TaxingPointValue)).Append(',').Append(N(g.Discount)).Append(',').Append(g.ForeignSource ? "Yes" : "No").Append(',')
                    .Append(g.TfnQuoted ? "Yes" : "No").Append(',').Append(N(g.TfnWithheld)).AppendLine();
        return sb.ToString();
    }
}

/// <summary>tblEssGrants, written through the procs in 2026-10-26-05-ess-employer-register.sql.</summary>
public sealed class EssGrantService(IDbConnectionFactory db)
{
    private sealed record Row(int Id, string Employee, string? EmployeeRef, string Scheme, string Interest, DateTime Acquired, double Interests,
        double MarketValue, double AmountPaid, double? ExercisePrice, DateTime? TaxingPoint, double? TaxingPointValue, bool TfnQuoted,
        double TfnWithheld, bool ForeignSource, string? Comment)
    {
        public EssGrant ToGrant() => new(Id, Employee, EmployeeRef, Scheme.Trim(), Interest.Trim(), DateOnly.FromDateTime(Acquired), (decimal)Interests,
            M(MarketValue), M(AmountPaid), ExercisePrice is { } e ? (decimal)e : null, TaxingPoint is { } t ? DateOnly.FromDateTime(t) : null,
            TaxingPointValue is { } v ? M(v) : null, TfnQuoted, M(TfnWithheld), ForeignSource, Comment);

        private static decimal M(double d) => Math.Round((decimal)d, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>The portfolio's grants. Null when the migration hasn't been run.</summary>
    public async Task<IReadOnlyList<EssGrant>?> GetAsync(int portfolioId)
    {
        await using var con = db.Create();
        try
        {
            var rows = await con.QueryAsync<Row>("""
                SELECT iEssGrantID AS Id, strEmployee AS Employee, strEmployeeRef AS EmployeeRef, strScheme AS Scheme, strInterest AS Interest,
                       CAST(dtmAcquired AS datetime) AS Acquired, dblInterests AS Interests, dblMarketValue AS MarketValue, dblAmountPaid AS AmountPaid,
                       dblExercisePrice AS ExercisePrice, CAST(dtmTaxingPoint AS datetime) AS TaxingPoint, dblTaxingPointValue AS TaxingPointValue,
                       bTfnQuoted AS TfnQuoted, dblTfnWithheld AS TfnWithheld, bForeignSource AS ForeignSource, strComment AS Comment
                FROM tblEssGrants WHERE iUserID = @portfolioId ORDER BY dtmAcquired DESC, strEmployee
                """, new { portfolioId });
            return rows.Select(r => r.ToGrant()).ToList();
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208)
        {
            return null;
        }
    }

    public async Task<ProcResult> SaveAsync(int portfolioId, EssGrant g)
    {
        await using var con = db.Create();
        var row = await con.QuerySingleAsync<(string Result, int? Id)>("uspSaveEssGrant", Parameters(portfolioId, g), commandType: CommandType.StoredProcedure);
        return ProcResult.From(row.Result, "Success", row.Id);
    }

    public static object Parameters(int portfolioId, EssGrant g) => new
    {
        iUserID = portfolioId, iEssGrantID = g.Id, strEmployee = g.Employee.Trim(), strEmployeeRef = g.EmployeeRef?.Trim(), strScheme = g.Scheme,
        strInterest = g.Interest, dtmAcquired = g.Acquired.ToDateTime(TimeOnly.MinValue), dblInterests = (double)g.Interests,
        dblMarketValue = (double)g.MarketValue, dblAmountPaid = (double)g.AmountPaid, dblExercisePrice = (double?)g.ExercisePrice,
        dtmTaxingPoint = g.TaxingPoint?.ToDateTime(TimeOnly.MinValue), dblTaxingPointValue = (double?)g.TaxingPointValue,
        bTfnQuoted = g.TfnQuoted, dblTfnWithheld = (double)g.TfnWithheld, bForeignSource = g.ForeignSource,
        strComment = string.IsNullOrWhiteSpace(g.Comment) ? null : g.Comment.Trim(),
    };

    public async Task<ProcResult> DeleteAsync(int portfolioId, int id)
    {
        await using var con = db.Create();
        var result = await con.ExecuteScalarAsync<string>("uspDeleteEssGrant", new { iUserID = portfolioId, iEssGrantID = id }, commandType: CommandType.StoredProcedure);
        return ProcResult.From(result, "Success");
    }
}
