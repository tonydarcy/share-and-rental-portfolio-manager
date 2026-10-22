using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Data;

namespace PortfolioManager.Core.SalesTax;

/// <summary>GST / VAT / sales tax: the portfolio's setup, its tax codes, and the report for a period.</summary>
public sealed class SalesTaxService(IDbConnectionFactory db)
{
    /// <summary>The portfolio's setup, or null if sales tax hasn't been set up (or the migration hasn't been run).</summary>
    public async Task<SalesTaxSetup?> GetSetupAsync(int portfolioId)
    {
        await using var con = db.Create();
        try
        {
            var row = (await con.QueryAsync("uspGetSalesTaxSetup", new { iUserId = portfolioId }, commandType: CommandType.StoredProcedure))
                .Cast<IDictionary<string, object?>>().FirstOrDefault();
            return MapSetup(row);
        }
        catch (SqlException ex) when (ex.Number == 2812) // proc not found: migration not run
        {
            return null;
        }
    }

    public static SalesTaxSetup? MapSetup(IDictionary<string, object?>? row)
    {
        if (row is null || Db.Int(row, "iControlAccountID") is not { } accountId)
            return null;
        var name = Db.Str(row, "strAccountName");
        return new SalesTaxSetup(
            Db.Str(row, "strTaxName") is { Length: > 0 } t ? t : "Sales tax",
            Db.Int(row, "iReportingMonths") ?? 3,
            accountId,
            Db.Int(row, "iAccountNumber") is { } n ? $"{n} - {name}" : name);
    }

    public async Task<ProcResult> SaveSetupAsync(int portfolioId, SalesTaxSetupInput input)
    {
        await using var con = db.Create();
        var result = await con.QuerySingleOrDefaultAsync<string?>("uspSaveSalesTaxSetup", new
        {
            iUserId = portfolioId,
            strTaxName = input.TaxName?.Trim() ?? "",
            iReportingMonths = input.ReportingMonths,
            iControlAccountID = input.ControlAccountId is > 0 ? input.ControlAccountId : null,
        }, commandType: CommandType.StoredProcedure);
        return ProcResult.From(result, "Success");
    }

    public async Task<IReadOnlyList<TaxCode>> GetCodesAsync(int portfolioId)
    {
        await using var con = db.Create();
        try
        {
            var rows = await con.QueryAsync("uspGetTaxCodes", new { iUserId = portfolioId }, commandType: CommandType.StoredProcedure);
            return MapCodes(rows.Cast<IDictionary<string, object?>>());
        }
        catch (SqlException ex) when (ex.Number == 2812)
        {
            return [];
        }
    }

    /// <summary>The codes a journal line or an allocation can use: the active ones.</summary>
    public async Task<IReadOnlyList<TaxCode>> GetActiveCodesAsync(int portfolioId) =>
        (await GetCodesAsync(portfolioId)).Where(c => c.IsActive).ToList();

    public static IReadOnlyList<TaxCode> MapCodes(IEnumerable<IDictionary<string, object?>> rows) =>
        rows.Select(r => new TaxCode(
                Db.Int(r, "iTaxCodeID") ?? 0,
                Db.Str(r, "strCode"),
                Db.Str(r, "strName"),
                Db.Str(r, "strKind") == TaxKind.Purchases ? TaxKind.Purchases : TaxKind.Sales,
                Db.Dec(r, "dblRate") ?? 0,
                Db.Str(r, "strAmountBoxes") is { Length: > 0 } a ? a : null,
                Db.Str(r, "strTaxBox") is { Length: > 0 } b ? b : null,
                Db.Bool(r, "bAmountIncludesTax") ?? true,
                Db.Bool(r, "bIsActive") ?? true,
                Db.Int(r, "iSortOrder") ?? 0,
                Db.Bool(r, "bInUse") ?? false))
            .Where(c => c.TaxCodeId > 0)
            .ToList();

    public async Task<ProcResult> SaveCodeAsync(int portfolioId, TaxCodeInput input)
    {
        await using var con = db.Create();
        var row = await con.QuerySingleOrDefaultAsync<(string? Result, int? TaxCodeId)>("uspSaveTaxCode",
            CodeParameters(portfolioId, input), commandType: CommandType.StoredProcedure);
        return ProcResult.From(row.Result, "Success", row.TaxCodeId);
    }

    public static object CodeParameters(int portfolioId, TaxCodeInput input) => new
    {
        iUserId = portfolioId,
        iTaxCodeID = input.TaxCodeId,
        strCode = input.Code?.Trim().ToUpperInvariant() ?? "",
        strName = input.Name?.Trim() ?? "",
        strKind = input.Kind,
        dblRate = input.Rate ?? 0,
        strAmountBoxes = string.Join(",", TaxCode.SplitBoxes(input.AmountBoxes)) is { Length: > 0 } a ? a : null,
        strTaxBox = string.IsNullOrWhiteSpace(input.TaxBox) ? null : input.TaxBox.Trim(),
        bAmountIncludesTax = input.AmountIncludesTax,
        bIsActive = input.IsActive,
        iSortOrder = input.SortOrder,
    };

    public async Task<ProcResult> DeleteCodeAsync(int portfolioId, int taxCodeId)
    {
        await using var con = db.Create();
        var result = await con.QuerySingleOrDefaultAsync<string?>("uspDeleteTaxCode",
            new { iUserId = portfolioId, iTaxCodeID = taxCodeId }, commandType: CommandType.StoredProcedure);
        return ProcResult.From(result, "Success");
    }

    /// <summary>
    /// Adds the preset's codes the portfolio doesn't have yet (matched by code), in one
    /// transaction. Returns how many were added.
    /// </summary>
    public async Task<int> AddPresetCodesAsync(int portfolioId, SalesTaxPreset preset)
    {
        var existing = (await GetCodesAsync(portfolioId)).Select(c => c.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var toAdd = PresetInputs(preset).Where(i => !existing.Contains(i.Code)).ToList();
        if (toAdd.Count == 0)
            return 0;

        await using var con = db.Create();
        await con.OpenAsync();
        await using var tx = await con.BeginTransactionAsync();
        foreach (var input in toAdd)
        {
            var row = await con.QuerySingleOrDefaultAsync<(string? Result, int? TaxCodeId)>("uspSaveTaxCode",
                CodeParameters(portfolioId, input), tx, commandType: CommandType.StoredProcedure);
            var result = ProcResult.From(row.Result, "Success");
            if (!result.Ok)
                throw new InvalidOperationException($"Tax code {input.Code} couldn't be added: {result.Error}");
        }
        await tx.CommitAsync();
        return toAdd.Count;
    }

    public static IReadOnlyList<TaxCodeInput> PresetInputs(SalesTaxPreset preset) =>
        preset.Codes.Select((c, i) => new TaxCodeInput
        {
            Code = c.Code,
            Name = c.Name,
            Kind = c.Kind,
            Rate = c.Rate,
            AmountBoxes = c.AmountBoxes,
            TaxBox = c.TaxBox,
            AmountIncludesTax = preset.AmountsIncludeTax,
            IsActive = true,
            SortOrder = (i + 1) * 10,
        }).ToList();

    /// <summary>The report for the period, from every journal line with one of the portfolio's tax codes.</summary>
    public async Task<SalesTaxReport> GetReportAsync(int portfolioId, string taxName, DateOnly from, DateOnly to)
    {
        var codes = await GetCodesAsync(portfolioId);
        await using var con = db.Create();
        var rows = await con.QueryAsync("uspGetSalesTaxLines", new
        {
            iUserId = portfolioId,
            dtFrom = from.ToDateTime(TimeOnly.MinValue),
            dtTo = to.ToDateTime(TimeOnly.MinValue),
        }, commandType: CommandType.StoredProcedure);
        return SalesTaxReport.Build(taxName, from, to, codes, MapLines(rows.Cast<IDictionary<string, object?>>(), codes));
    }

    /// <summary>
    /// The proc's Net and Tax are debit-positive. Sales are credits, so they're turned round:
    /// a sale is positive and a refund given is negative; a purchase is positive and a refund
    /// received negative.
    /// </summary>
    public static IReadOnlyList<SalesTaxLine> MapLines(IEnumerable<IDictionary<string, object?>> rows, IReadOnlyList<TaxCode> codes)
    {
        var sales = codes.Where(c => c.IsSales).Select(c => c.TaxCodeId).ToHashSet();
        return rows.Select(r =>
            {
                var codeId = Db.Int(r, "iTaxCodeID") ?? 0;
                var sign = sales.Contains(codeId) ? -1 : 1;
                var account = Db.Str(r, "strAccountName");
                return new SalesTaxLine(
                    Db.Int(r, "iJournalID") ?? 0,
                    Db.Date(r, "dtmTransactionDate") ?? default,
                    Db.Str(r, "strDescription") is { Length: > 0 } d ? d : Db.Str(r, "strJournalDescription"),
                    Db.Str(r, "strSourceModule"),
                    Db.Int(r, "iAccountNumber") is { } n ? $"{n} - {account}" : account,
                    codeId,
                    sign * (Db.Dec(r, "Net") ?? 0),
                    sign * (Db.Dec(r, "Tax") ?? 0));
            })
            .Where(l => l.JournalId > 0)
            .ToList();
    }
}
