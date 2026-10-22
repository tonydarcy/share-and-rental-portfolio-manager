using System.Data;
using Dapper;
using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Data;

namespace PortfolioManager.Core.TaxWork;

/// <summary>
/// Reconciliation items the ledger can't tell. AddBack: an expense in the accounts that isn't
/// deductible (fines, entertainment, capital costs). Assessable: income the accounts don't show.
/// NonAssessable: income in the accounts that isn't taxed. Deduction: a deduction not in the accounts.
/// </summary>
public static class TaxAdjustmentKinds
{
    public const string AddBack = "AddBack";
    public const string Assessable = "Assessable";
    public const string NonAssessable = "NonAssessable";
    public const string Deduction = "Deduction";

    public static readonly IReadOnlyList<string> All = [AddBack, Assessable, NonAssessable, Deduction];

    public static string Label(string kind) => kind switch
    {
        AddBack => "Add: expense not deductible",
        Assessable => "Add: income not in the accounts",
        NonAssessable => "Less: income not assessable",
        Deduction => "Less: deduction not in the accounts",
        _ => kind,
    };

    /// <summary>+1 when the adjustment increases taxable income.</summary>
    public static int Sign(string kind) => kind is AddBack or Assessable ? 1 : -1;
}

public sealed record TaxAdjustment(int AdjustmentId, string Kind, string Description, decimal Amount)
{
    public decimal Signed => TaxAdjustmentKinds.Sign(Kind) * Amount;
}

/// <summary>
/// The year's figures the ledger can't supply. CapitalLossesBroughtForward is null when it's worked
/// out from the trades; TaxRatePercent null means 30% for a company.
/// </summary>
public sealed record TaxYearSettings(
    decimal TaxLossesBroughtForward,
    decimal? CapitalLossesBroughtForward,
    decimal? TaxRatePercent,
    decimal? EcpiPercent,
    decimal PaygInstalments,
    DateOnly? ResolutionDate)
{
    public static readonly TaxYearSettings Default = new(0, null, null, null, 0, null);
}

public sealed record TaxYearData(TaxYearSettings Settings, IReadOnlyList<TaxAdjustment> Adjustments)
{
    public static readonly TaxYearData Empty = new(TaxYearSettings.Default, []);
}

public sealed class TaxYearInput
{
    public decimal TaxLossesBroughtForward { get; set; }
    public decimal? CapitalLossesBroughtForward { get; set; }
    public decimal? TaxRatePercent { get; set; }
    public decimal? EcpiPercent { get; set; }
    public decimal PaygInstalments { get; set; }
    public DateTime? ResolutionDate { get; set; }

    public static TaxYearInput From(TaxYearSettings s) => new()
    {
        TaxLossesBroughtForward = s.TaxLossesBroughtForward,
        CapitalLossesBroughtForward = s.CapitalLossesBroughtForward,
        TaxRatePercent = s.TaxRatePercent,
        EcpiPercent = s.EcpiPercent,
        PaygInstalments = s.PaygInstalments,
        ResolutionDate = s.ResolutionDate?.ToDateTime(TimeOnly.MinValue),
    };
}

public sealed class TaxAdjustmentInput
{
    public string Kind { get; set; } = TaxAdjustmentKinds.AddBack;
    public string Description { get; set; } = "";
    public decimal Amount { get; set; }
}

/// <summary>Reads and saves a year's tax settings and adjustments (tblTaxYear, tblTaxAdjustment).</summary>
public sealed class TaxYearService(IDbConnectionFactory db)
{
    public async Task<TaxYearData> GetAsync(int portfolioId, int year)
    {
        await using var con = db.Create();
        await using var grid = await con.QueryMultipleAsync("uspGetTaxYear", new { iUserId = portfolioId, iYear = year },
            commandType: CommandType.StoredProcedure);
        var settings = (await grid.ReadAsync()).Cast<IDictionary<string, object?>>().FirstOrDefault();
        var adjustments = grid.IsConsumed ? [] : (await grid.ReadAsync()).Cast<IDictionary<string, object?>>().ToList();
        return Map(settings, adjustments);
    }

    public static TaxYearData Map(IDictionary<string, object?>? settings, IEnumerable<IDictionary<string, object?>> adjustments) => new(
        settings is null ? TaxYearSettings.Default : new TaxYearSettings(
            Db.Dec(settings, "dblTaxLossesBroughtForward") ?? 0,
            Db.Dec(settings, "dblCapitalLossesBroughtForward"),
            Db.Dec(settings, "dblTaxRate"),
            Db.Dec(settings, "dblEcpiPercent"),
            Db.Dec(settings, "dblPaygInstalments") ?? 0,
            Db.Date(settings, "dtResolutionDate")),
        adjustments.Select(r => new TaxAdjustment(
                Db.Int(r, "iAdjustmentID") ?? 0,
                Db.Str(r, "strKind"),
                Db.Str(r, "strDescription"),
                Db.Dec(r, "dblAmount") ?? 0))
            .Where(a => a.AdjustmentId > 0 && TaxAdjustmentKinds.All.Contains(a.Kind))
            .ToList());

    public async Task<ProcResult> SaveAsync(int portfolioId, int year, DateOnly yearEnd, TaxYearInput input)
    {
        await using var con = db.Create();
        var result = await con.QuerySingleOrDefaultAsync<string?>("uspSaveTaxYear", new
        {
            iUserId = portfolioId,
            iYear = year,
            dtYearEnd = yearEnd.ToDateTime(TimeOnly.MinValue),
            dblTaxLossesBroughtForward = input.TaxLossesBroughtForward,
            dblCapitalLossesBroughtForward = input.CapitalLossesBroughtForward,
            dblTaxRate = input.TaxRatePercent,
            dblEcpiPercent = input.EcpiPercent,
            dblPaygInstalments = input.PaygInstalments,
            dtResolutionDate = input.ResolutionDate?.Date,
        }, commandType: CommandType.StoredProcedure);
        return ProcResult.From(result, "Success");
    }

    public async Task<ProcResult> AddAdjustmentAsync(int portfolioId, int year, DateOnly yearEnd, TaxAdjustmentInput input)
    {
        await using var con = db.Create();
        var row = await con.QuerySingleOrDefaultAsync<(string? Result, int? AdjustmentId)>("uspSaveTaxAdjustment", new
        {
            iUserId = portfolioId,
            iYear = year,
            dtYearEnd = yearEnd.ToDateTime(TimeOnly.MinValue),
            strKind = input.Kind,
            strDescription = input.Description?.Trim() ?? "",
            dblAmount = input.Amount,
        }, commandType: CommandType.StoredProcedure);
        return ProcResult.From(row.Result, "Success", row.AdjustmentId);
    }

    public async Task<ProcResult> DeleteAdjustmentAsync(int portfolioId, int year, DateOnly yearEnd, int adjustmentId)
    {
        await using var con = db.Create();
        var result = await con.QuerySingleOrDefaultAsync<string?>("uspDeleteTaxAdjustment", new
        {
            iUserId = portfolioId,
            iYear = year,
            dtYearEnd = yearEnd.ToDateTime(TimeOnly.MinValue),
            iAdjustmentID = adjustmentId,
        }, commandType: CommandType.StoredProcedure);
        return ProcResult.From(result, "Success");
    }
}
