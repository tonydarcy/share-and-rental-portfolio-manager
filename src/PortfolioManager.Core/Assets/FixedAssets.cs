using System.Data;
using System.Globalization;
using System.Xml.Linq;
using Dapper;
using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Data;
using PortfolioManager.Core.Regions;

namespace PortfolioManager.Core.Assets;

/// <summary>An asset in the register, with the depreciation posted for it so far (the opening amount plus every run).</summary>
public sealed record FixedAsset(
    int Id,
    string? Number,
    string Name,
    string? Description,
    DateOnly Purchased,
    DateOnly Start,
    decimal Cost,
    string Method,
    decimal? LifeYears,
    DateOnly? OpeningDate,
    decimal OpeningAccum,
    int AssetAccountId,
    int AccumAccountId,
    int ExpenseAccountId,
    DateOnly? Disposed,
    decimal? Proceeds,
    int? ProceedsAccountId,
    decimal? DisposalDepreciation,
    int? DisposalJournalId,
    decimal Posted,
    DateOnly? LastRun)
{
    public bool IsDisposed => Disposed is not null;

    /// <summary>All the depreciation in the books for it: opening, runs and (once sold) the depreciation to the sale.</summary>
    public decimal Accumulated => OpeningAccum + Posted + (DisposalDepreciation ?? 0);

    public decimal BookValue => IsDisposed ? 0 : Cost - Accumulated;

    public decimal? GainOnSale => Proceeds is { } p ? DepreciationEngine.GainOnSale(p, Cost, Accumulated) : null;

    public string Display => string.IsNullOrWhiteSpace(Number) ? Name : $"{Number} - {Name}";

    public DepreciableAsset ForEngine => new(Cost, Method, LifeYears, Start, OpeningDate, OpeningAccum, Disposed);
}

public sealed record DepreciationRun(int Id, DateOnly To, int? JournalId, decimal Total, int Assets);

/// <summary>One asset's line in a depreciation run: the depreciation to the date, what is already posted, and the difference to post.</summary>
public sealed record DepreciationLine(FixedAsset Asset, decimal ToDate, decimal Posted)
{
    public decimal Amount => Math.Max(0, ToDate - Posted);
}

/// <summary>What selling an asset would post: the depreciation since the last run, the written-down value and the gain or loss.</summary>
public sealed record DisposalPreview(decimal FinalDepreciation, decimal WrittenDown, decimal Proceeds)
{
    public decimal Gain => Proceeds - WrittenDown;
}

/// <summary>The add / edit asset form.</summary>
public sealed class FixedAssetInput
{
    public string? Number { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public DateTime Purchased { get; set; }
    public DateTime? Start { get; set; }
    public decimal Cost { get; set; }
    public string Method { get; set; } = DepreciationMethods.DiminishingValue;
    public decimal? LifeYears { get; set; }
    public DateTime? OpeningDate { get; set; }
    public decimal OpeningAccum { get; set; }
    public int AssetAccountId { get; set; }
    public int AccumAccountId { get; set; }
    public int ExpenseAccountId { get; set; }

    public static FixedAssetInput From(FixedAsset a) => new()
    {
        Number = a.Number, Name = a.Name, Description = a.Description, Purchased = a.Purchased.ToDateTime(TimeOnly.MinValue),
        Start = a.Start.ToDateTime(TimeOnly.MinValue), Cost = a.Cost, Method = a.Method, LifeYears = a.LifeYears,
        OpeningDate = a.OpeningDate?.ToDateTime(TimeOnly.MinValue), OpeningAccum = a.OpeningAccum, AssetAccountId = a.AssetAccountId,
        AccumAccountId = a.AccumAccountId, ExpenseAccountId = a.ExpenseAccountId,
    };

    public DateOnly StartDate => DateOnly.FromDateTime((Start ?? Purchased).Date);

    public IReadOnlyList<string> Validate(IReadOnlySet<int> accounts, bool australia)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Name))
            errors.Add("Enter the asset's name.");
        else if (Name.Trim().Length > 200)
            errors.Add("The name can be at most 200 characters.");
        if (Purchased == default)
            errors.Add("Enter the purchase date.");
        else if (Start is { } s && s.Date < Purchased.Date)
            errors.Add("It can't be first used before it was bought.");
        if (Cost <= 0)
            errors.Add("Enter the cost.");
        if (OpeningAccum < 0 || OpeningAccum > Cost)
            errors.Add("The depreciation to the opening date must be between nil and the cost.");
        if (OpeningAccum > 0 && OpeningDate is null)
            errors.Add("Enter the date the opening depreciation runs to.");
        if (OpeningDate is { } o && Purchased != default && o.Date < Purchased.Date)
            errors.Add("The opening date can't be before the purchase date.");
        if (!australia && DepreciationMethods.IsAustralianOnly(Method))
            errors.Add("The pools and the instant asset write-off are Australian tax rules. Choose diminishing value or prime cost.");
        if (!accounts.Contains(AssetAccountId) || !accounts.Contains(AccumAccountId) || !accounts.Contains(ExpenseAccountId))
            errors.Add("Choose the asset, accumulated depreciation and depreciation expense accounts.");
        if (Cost > 0)
            errors.AddRange(DepreciationEngine.Check(new DepreciableAsset(Cost, Method, LifeYears, StartDate, null, OpeningAccum)));
        return errors;
    }
}

public static class FixedAssetRules
{
    /// <summary>Each asset still held (and not sold by then) with the depreciation to post to the date.</summary>
    public static IReadOnlyList<DepreciationLine> Run(IEnumerable<FixedAsset> assets, DateOnly to, YearStart? yearStart = null) =>
        assets.Where(a => !a.IsDisposed && a.Method != DepreciationMethods.None)
            .Select(a => new DepreciationLine(a, Math.Min(a.Cost, DepreciationEngine.Accumulated(a.ForEngine with { Disposed = null }, to, yearStart)),
                a.OpeningAccum + a.Posted))
            .Where(l => l.Amount > 0)
            .ToList();

    public static DisposalPreview Disposal(FixedAsset a, DateOnly date, decimal proceeds, YearStart? yearStart = null)
    {
        var toDate = Math.Min(a.Cost, DepreciationEngine.Accumulated(a.ForEngine with { Disposed = date }, date, yearStart));
        var final = Math.Max(0, toDate - a.OpeningAccum - a.Posted);
        return new DisposalPreview(final, a.Cost - a.OpeningAccum - a.Posted - final, proceeds);
    }

    public static string RunXml(IEnumerable<DepreciationLine> lines) =>
        new XElement("assets", lines.Select(l => new XElement("a",
            new XAttribute("id", l.Asset.Id),
            new XAttribute("v", l.Amount.ToString(CultureInfo.InvariantCulture))))).ToString(SaveOptions.DisableFormatting);
}

/// <summary>The fixed asset register, depreciation runs and disposals (2026-10-21-29-fixed-assets.sql).</summary>
public sealed class FixedAssetService(IDbConnectionFactory db)
{
    public const int AssetAccount = 1500, AccumAccount = 1590, ExpenseAccount = 5750;

    public async Task<IReadOnlyList<FixedAsset>> GetAsync(int portfolioId, int? id = null)
    {
        await using var con = db.Create();
        try
        {
            var rows = await con.QueryAsync("""
                SELECT A.*,
                       ISNULL((SELECT SUM(E.dblAmount) FROM tblDepreciationEntries E WHERE E.iAssetID = A.iAssetID), 0) AS Posted,
                       (SELECT MAX(R.dtmTo) FROM tblDepreciationRuns R JOIN tblDepreciationEntries E ON E.iRunID = R.iRunID WHERE E.iAssetID = A.iAssetID) AS LastRun
                FROM tblFixedAssets A
                WHERE A.iUserID = @portfolioId AND (@id IS NULL OR A.iAssetID = @id)
                ORDER BY CASE WHEN A.dtmDisposed IS NULL THEN 0 ELSE 1 END, A.strNumber, A.strName
                """, new { portfolioId, id });
            return rows.Cast<IDictionary<string, object?>>().Select(Map).ToList();
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208)
        {
            return [];
        }
    }

    public async Task<FixedAsset?> GetOneAsync(int portfolioId, int id) => (await GetAsync(portfolioId, id)).FirstOrDefault();

    public static FixedAsset Map(IDictionary<string, object?> r)
    {
        string? Optional(string column) => Db.Value(r, column) is null ? null : Db.Str(r, column);
        return new FixedAsset(
            Db.Int(r, "iAssetID") ?? 0,
            Optional("strNumber"),
            Db.Str(r, "strName"),
            Optional("strDescription"),
            Db.Date(r, "dtmPurchaseDate") ?? default,
            Db.Date(r, "dtmStartDate") ?? default,
            Db.Dec(r, "dblCost") ?? 0,
            Db.Str(r, "strMethod"),
            Db.Dec(r, "dblLifeYears"),
            Db.Date(r, "dtmOpeningDate"),
            Db.Dec(r, "dblOpeningAccum") ?? 0,
            Db.Int(r, "iAssetAccountID") ?? 0,
            Db.Int(r, "iAccumAccountID") ?? 0,
            Db.Int(r, "iExpenseAccountID") ?? 0,
            Db.Date(r, "dtmDisposed"),
            Db.Dec(r, "dblProceeds"),
            Db.Int(r, "iProceedsAccountID"),
            Db.Dec(r, "dblDisposalDepreciation"),
            Db.Int(r, "iDisposalJournalID"),
            Db.Dec(r, "Posted") ?? 0,
            Db.Date(r, "LastRun"));
    }

    /// <summary>The portfolio's (or the standard) 1500, 1590 and 5750 accounts, for a new asset's defaults.</summary>
    public async Task<(int Asset, int Accum, int Expense)> DefaultAccountsAsync(int portfolioId)
    {
        await using var con = db.Create();
        var ids = (await con.QueryAsync<(int Number, int Id)>("""
            SELECT iAccountNumber, iAccountID FROM tblChartOfAccounts WHERE iAccountNumber IN (1500, 1590, 5750) AND (iUserID = @portfolioId OR iUserID = 0)
            ORDER BY iUserID
            """, new { portfolioId })).GroupBy(a => a.Number).ToDictionary(g => g.Key, g => g.Last().Id);
        return (ids.GetValueOrDefault(AssetAccount), ids.GetValueOrDefault(AccumAccount), ids.GetValueOrDefault(ExpenseAccount));
    }

    public async Task<ProcResult> SaveAsync(int portfolioId, int? id, FixedAssetInput input, IReadOnlySet<int> accounts, bool australia)
    {
        if (input.Validate(accounts, australia) is [var first, ..])
            return ProcResult.Failed(first);
        await using var con = db.Create();
        var row = await con.QuerySingleAsync<(string Result, int? Id)>("uspSaveFixedAsset", SaveParameters(portfolioId, id, input),
            commandType: CommandType.StoredProcedure);
        return ProcResult.From(row.Result, "Success", row.Id);
    }

    public static object SaveParameters(int portfolioId, int? id, FixedAssetInput input) => new
    {
        iUserId = portfolioId,
        iAssetID = id ?? 0,
        strNumber = string.IsNullOrWhiteSpace(input.Number) ? null : input.Number.Trim(),
        strName = input.Name?.Trim(),
        strDescription = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim(),
        dtPurchaseDate = input.Purchased.Date,
        dtStartDate = (input.Start ?? input.Purchased).Date,
        dblCost = input.Cost,
        strMethod = input.Method,
        dblLifeYears = DepreciationMethods.NeedsLife(input.Method) ? input.LifeYears : null,
        dtOpeningDate = input.OpeningDate?.Date,
        dblOpeningAccum = input.OpeningAccum,
        iAssetAccountID = input.AssetAccountId,
        iAccumAccountID = input.AccumAccountId,
        iExpenseAccountID = input.ExpenseAccountId,
    };

    public async Task<ProcResult> DeleteAsync(int portfolioId, int id) =>
        await RunAsync("uspDeleteFixedAsset", new { iUserId = portfolioId, iAssetID = id });

    public async Task<IReadOnlyList<DepreciationRun>> GetRunsAsync(int portfolioId)
    {
        await using var con = db.Create();
        try
        {
            var rows = await con.QueryAsync<(int Id, DateTime To, int? Journal, decimal Total, int Count)>("""
                SELECT R.iRunID, R.dtmTo, R.iJournalID, ISNULL(SUM(E.dblAmount), 0), COUNT(E.iAssetID)
                FROM tblDepreciationRuns R LEFT JOIN tblDepreciationEntries E ON E.iRunID = R.iRunID
                WHERE R.iUserID = @portfolioId GROUP BY R.iRunID, R.dtmTo, R.iJournalID ORDER BY R.dtmTo DESC
                """, new { portfolioId });
            return rows.Select(r => new DepreciationRun(r.Id, DateOnly.FromDateTime(r.To), r.Journal, r.Total, r.Count)).ToList();
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208)
        {
            return [];
        }
    }

    /// <summary>Posts the depreciation for every asset up to the date in one journal.</summary>
    public async Task<ProcResult> PostRunAsync(int portfolioId, DateOnly to, YearStart? yearStart = null)
    {
        var lines = FixedAssetRules.Run(await GetAsync(portfolioId), to, yearStart);
        if (lines.Count == 0)
            return ProcResult.Failed("There is no depreciation to post to that date.");
        await using var con = db.Create();
        var row = await con.QuerySingleAsync<(string Result, int? Id)>("uspPostDepreciationRun",
            new { iUserId = portfolioId, dtTo = to.ToDateTime(TimeOnly.MinValue), xmlAssets = FixedAssetRules.RunXml(lines) },
            commandType: CommandType.StoredProcedure);
        return ProcResult.From(row.Result, "Success", row.Id);
    }

    public async Task<ProcResult> DeleteRunAsync(int portfolioId, int runId) =>
        await RunAsync("uspDeleteDepreciationRun", new { iUserId = portfolioId, iRunID = runId });

    public async Task<ProcResult> DisposeAsync(int portfolioId, FixedAsset asset, DateOnly date, decimal proceeds, int? proceedsAccountId,
        YearStart? yearStart = null)
    {
        var preview = FixedAssetRules.Disposal(asset, date, proceeds, yearStart);
        return await RunAsync("uspDisposeFixedAsset", new
        {
            iUserId = portfolioId, iAssetID = asset.Id, dtDate = date.ToDateTime(TimeOnly.MinValue), dblProceeds = proceeds,
            iProceedsAccountID = proceeds > 0 ? proceedsAccountId : null, dblFinalDepreciation = preview.FinalDepreciation,
        });
    }

    public async Task<ProcResult> UndoDisposalAsync(int portfolioId, int id) =>
        await RunAsync("uspUndoAssetDisposal", new { iUserId = portfolioId, iAssetID = id });

    private async Task<ProcResult> RunAsync(string proc, object parameters)
    {
        await using var con = db.Create();
        return ProcResult.From(await con.ExecuteScalarAsync<string>(proc, parameters, commandType: CommandType.StoredProcedure), "Success");
    }
}
