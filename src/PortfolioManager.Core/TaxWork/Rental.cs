using System.Data;
using System.Globalization;
using System.Xml.Linq;
using Dapper;
using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Data;
using PortfolioManager.Core.Regions;
using PortfolioManager.Core.Tax;

namespace PortfolioManager.Core.TaxWork;

/// <summary>Where a rental category goes on the individual return (item 21).</summary>
public enum RentalLabel { Income, Interest, CapitalWorks, Other, None }

public sealed record RentalCategory(string Key, string Name, RentalLabel Label, string? Hint = null);

/// <summary>The categories of the ATO rental property schedule. Capital allowances and capital works are worked out from the assets.</summary>
public static class RentalCategories
{
    public const string GrossRent = "GrossRent", OtherIncome = "OtherIncome", Interest = "Interest", Weeks = "Weeks";

    public static readonly IReadOnlyList<RentalCategory> Income =
    [
        new(GrossRent, "Gross rent", RentalLabel.Income),
        new(OtherIncome, "Other rental-related income", RentalLabel.Income, "Insurance payouts for lost rent, bond money kept, letting fees"),
    ];

    public static readonly IReadOnlyList<RentalCategory> Expenses =
    [
        new("Advertising", "Advertising for tenants", RentalLabel.Other),
        new("BodyCorporate", "Body corporate fees and charges", RentalLabel.Other, "Not special levies for capital works"),
        new("Borrowing", "Borrowing expenses", RentalLabel.Other, "This year's share: costs over $100 are spread over 5 years or the loan term"),
        new("Cleaning", "Cleaning", RentalLabel.Other),
        new("CouncilRates", "Council rates", RentalLabel.Other),
        new("Gardening", "Gardening and lawn mowing", RentalLabel.Other),
        new("Insurance", "Insurance", RentalLabel.Other),
        new(Interest, "Interest on loans", RentalLabel.Interest, "Only the part of the loan used for the property"),
        new("LandTax", "Land tax", RentalLabel.Other),
        new("Legal", "Legal fees", RentalLabel.Other, "Revenue legal costs only, such as evicting a tenant"),
        new("PestControl", "Pest control", RentalLabel.Other),
        new("AgentFees", "Property agent fees and commission", RentalLabel.Other),
        new("Repairs", "Repairs and maintenance", RentalLabel.Other, "Not initial repairs or improvements, which are capital"),
        new("Stationery", "Stationery, telephone and postage", RentalLabel.Other),
        new("Water", "Water charges", RentalLabel.Other),
        new("Sundry", "Sundry rental expenses", RentalLabel.Other),
    ];

    public static readonly IReadOnlyList<RentalCategory> All = [.. Income, .. Expenses];

    public static RentalCategory? Find(string key) => All.FirstOrDefault(c => c.Key == key);
}

public static class RentalAssetKinds
{
    public const string Plant = "Plant", Building = "Building";
    public const string DiminishingValue = "DV", PrimeCost = "PC";
}

public sealed record RentalProperty(int Id, string Address, DateOnly? FirstEarned, decimal OwnershipPct, bool Active, string? Note);

public sealed record RentalAsset(int Id, int PropertyId, string Kind, string Description, DateOnly Start, decimal Cost, decimal? LifeYears,
    string? Method, decimal? Rate, DateOnly? Disposed)
{
    public bool IsPlant => Kind == RentalAssetKinds.Plant;
}

/// <summary>One asset's deduction for a year: the opening value (undeducted cost), the year's deduction and the closing value.</summary>
public sealed record AssetYear(RentalAsset Asset, decimal Opening, int Days, decimal Deduction)
{
    public decimal Closing => Opening - Deduction;
}

/// <summary>
/// Decline in value (Division 40) and capital works (Division 43), worked out year by year from the start date.
/// Days are counted in the financial year and divided by 365, as in the ATO formulas.
/// </summary>
public static class RentalDepreciation
{
    /// <summary>Days the asset is held in the year: from the later of its start and the year start to the earlier of its disposal and the year end.</summary>
    public static int DaysHeld(RentalAsset a, DateOnly yearStart, DateOnly yearEnd)
    {
        var from = a.Start > yearStart ? a.Start : yearStart;
        var to = a.Disposed is { } d && d < yearEnd ? d : yearEnd;
        return to < from ? 0 : to.DayNumber - from.DayNumber + 1;
    }

    /// <summary>
    /// Diminishing value: opening value × days/365 × 200% / effective life. Prime cost: cost × days/365 × 100% / effective life.
    /// Capital works: cost × rate × days/365. None goes below nil.
    /// </summary>
    public static decimal YearDeduction(RentalAsset a, decimal opening, int days)
    {
        if (days <= 0 || opening <= 0)
            return 0;
        var amount = a.Kind switch
        {
            RentalAssetKinds.Plant when a.LifeYears is > 0 && a.Method == RentalAssetKinds.DiminishingValue =>
                Assets.DepreciationEngine.Diminishing(opening, days, a.LifeYears.Value),
            RentalAssetKinds.Plant when a.LifeYears is > 0 => Assets.DepreciationEngine.Prime(a.Cost, days, a.LifeYears.Value),
            RentalAssetKinds.Building when a.Rate is > 0 => a.Cost * days / 365m * a.Rate.Value / 100m,
            _ => 0,
        };
        return Math.Min(Math.Round(amount, 2), opening);
    }

    /// <summary>The asset's figures for the year ending endYear, after every earlier year's deduction.</summary>
    public static AssetYear For(RentalAsset a, int endYear, YearStart? start = null)
    {
        if (endYear < FinancialYear.EndYearFor(a.Start, start))
            return new AssetYear(a, 0, 0, 0);
        var opening = a.Cost;
        for (var y = FinancialYear.EndYearFor(a.Start, start); y < endYear; y++)
            opening -= YearDeduction(a, opening, DaysHeld(a, FinancialYear.StartDate(y, start), FinancialYear.EndDate(y, start)));
        var days = DaysHeld(a, FinancialYear.StartDate(endYear, start), FinancialYear.EndDate(endYear, start));
        return new AssetYear(a, opening, days, YearDeduction(a, opening, days));
    }
}

/// <summary>A property's schedule for a year: the whole property's figures and the owner's share.</summary>
public sealed record RentalPropertyYear(RentalProperty Property, int Year, IReadOnlyDictionary<string, decimal> Amounts, IReadOnlyList<AssetYear> Assets)
{
    private decimal Share(decimal whole) => Math.Round(whole * Property.OwnershipPct / 100m, 2);

    public decimal Weeks => Amounts.GetValueOrDefault(RentalCategories.Weeks);
    public decimal CapitalAllowances => Assets.Where(a => a.Asset.IsPlant).Sum(a => a.Deduction);
    public decimal CapitalWorks => Assets.Where(a => !a.Asset.IsPlant).Sum(a => a.Deduction);

    private decimal Whole(RentalLabel label) =>
        RentalCategories.All.Where(c => c.Label == label).Sum(c => Amounts.GetValueOrDefault(c.Key));

    public decimal GrossRent => Share(Whole(RentalLabel.Income));
    public decimal Interest => Share(Whole(RentalLabel.Interest));
    public decimal CapitalWorksShare => Share(CapitalWorks);
    public decimal OtherDeductions => Share(Whole(RentalLabel.Other) + CapitalAllowances);
    public decimal Net => GrossRent - Interest - CapitalWorksShare - OtherDeductions;
}

/// <summary>Item 21 for all the individual's properties.</summary>
public sealed record RentalSchedule(int Year, IReadOnlyList<RentalPropertyYear> Properties)
{
    public static readonly RentalSchedule None = new(0, []);

    public decimal GrossRent => Properties.Sum(p => p.GrossRent);
    public decimal Interest => Properties.Sum(p => p.Interest);
    public decimal CapitalWorks => Properties.Sum(p => p.CapitalWorksShare);
    public decimal OtherDeductions => Properties.Sum(p => p.OtherDeductions);
    public decimal Net => GrossRent - Interest - CapitalWorks - OtherDeductions;
}

public static class RentalAmountsXml
{
    public static string Write(IReadOnlyDictionary<string, decimal> amounts) =>
        string.Concat(amounts.Where(a => a.Value != 0)
            .Select(a => new XElement("a", new XAttribute("c", a.Key), new XAttribute("v", a.Value.ToString(CultureInfo.InvariantCulture))).ToString(SaveOptions.DisableFormatting)));
}

/// <summary>Rental properties (2026-10-21-19-rental-properties.sql).</summary>
public sealed class RentalService(IDbConnectionFactory db)
{
    public async Task<IReadOnlyList<RentalProperty>> GetPropertiesAsync(int portfolioId)
    {
        await using var con = db.Create();
        try
        {
            var rows = await con.QueryAsync("""
                SELECT iPropertyID, strAddress, dtmFirstEarned, dblOwnershipPct, bActive, strNote
                FROM tblRentalProperties WHERE iUserID = @portfolioId ORDER BY bActive DESC, strAddress
                """, new { portfolioId });
            return rows.Cast<IDictionary<string, object?>>().Select(r => new RentalProperty(Db.Int(r, "iPropertyID") ?? 0, Db.Str(r, "strAddress"),
                Db.Date(r, "dtmFirstEarned"), Db.Dec(r, "dblOwnershipPct") ?? 100, Db.Bool(r, "bActive") ?? true, Db.Str(r, "strNote") is { Length: > 0 } n ? n : null)).ToList();
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208)
        {
            return [];
        }
    }

    /// <summary>Every property's schedule for the year; properties marked inactive are included only when they have figures.</summary>
    public async Task<RentalSchedule> GetYearAsync(int portfolioId, int year)
    {
        var properties = await GetPropertiesAsync(portfolioId);
        if (properties.Count == 0)
            return RentalSchedule.None with { Year = year };
        await using var con = db.Create();
        var amounts = (await con.QueryAsync("""
                SELECT A.iPropertyID, A.strCategory, A.dblAmount FROM tblRentalAmounts A
                JOIN tblRentalProperties P ON P.iPropertyID = A.iPropertyID WHERE P.iUserID = @portfolioId AND A.iYear = @year
                """, new { portfolioId, year }))
            .Cast<IDictionary<string, object?>>()
            .GroupBy(r => Db.Int(r, "iPropertyID") ?? 0)
            .ToDictionary(g => g.Key, g => (IReadOnlyDictionary<string, decimal>)g.ToDictionary(r => Db.Str(r, "strCategory"), r => Db.Dec(r, "dblAmount") ?? 0));
        var assets = (await GetAssetsAsync(con, portfolioId)).GroupBy(a => a.PropertyId).ToDictionary(g => g.Key, g => g.ToList());
        var list = properties.Select(p => new RentalPropertyYear(p, year, amounts.GetValueOrDefault(p.Id) ?? new Dictionary<string, decimal>(),
                (assets.GetValueOrDefault(p.Id) ?? []).Select(a => RentalDepreciation.For(a, year)).Where(a => a.Opening > 0 || a.Deduction > 0).ToList()))
            .Where(p => p.Property.Active || p.Amounts.Count > 0 || p.Assets.Any(a => a.Deduction > 0))
            .ToList();
        return new RentalSchedule(year, list);
    }

    private static async Task<IReadOnlyList<RentalAsset>> GetAssetsAsync(System.Data.Common.DbConnection con, int portfolioId)
    {
        var rows = await con.QueryAsync("""
            SELECT A.iAssetID, A.iPropertyID, A.strKind, A.strDescription, A.dtmStart, A.dblCost, A.dblLifeYears, A.strMethod, A.dblRate, A.dtmDisposed
            FROM tblRentalAssets A JOIN tblRentalProperties P ON P.iPropertyID = A.iPropertyID
            WHERE P.iUserID = @portfolioId ORDER BY A.dtmStart, A.iAssetID
            """, new { portfolioId });
        return rows.Cast<IDictionary<string, object?>>().Select(r => new RentalAsset(Db.Int(r, "iAssetID") ?? 0, Db.Int(r, "iPropertyID") ?? 0,
            Db.Str(r, "strKind"), Db.Str(r, "strDescription"), Db.Date(r, "dtmStart") ?? default, Db.Dec(r, "dblCost") ?? 0, Db.Dec(r, "dblLifeYears"),
            Db.Str(r, "strMethod") is { Length: > 0 } m ? m : null, Db.Dec(r, "dblRate"), Db.Date(r, "dtmDisposed"))).ToList();
    }

    public async Task<ProcResult> SavePropertyAsync(int portfolioId, int id, string? address, DateOnly? firstEarned, decimal ownershipPct, bool active, string? note)
    {
        await using var con = db.Create();
        var row = await con.QuerySingleAsync<(string Result, int? Id)>("uspSaveRentalProperty",
            new { iUserID = portfolioId, iPropertyID = id, strAddress = address, dtmFirstEarned = firstEarned?.ToDateTime(TimeOnly.MinValue),
                dblOwnershipPct = ownershipPct, bActive = active, strNote = note },
            commandType: CommandType.StoredProcedure);
        return ProcResult.From(row.Result, "Success", row.Id);
    }

    public async Task<ProcResult> DeletePropertyAsync(int portfolioId, int id)
    {
        await using var con = db.Create();
        return ProcResult.From(await con.ExecuteScalarAsync<string>("uspDeleteRentalProperty", new { iUserID = portfolioId, iPropertyID = id },
            commandType: CommandType.StoredProcedure), "Success");
    }

    public async Task<ProcResult> SaveAmountsAsync(int portfolioId, int propertyId, int year, IReadOnlyDictionary<string, decimal> amounts)
    {
        await using var con = db.Create();
        return ProcResult.From(await con.ExecuteScalarAsync<string>("uspSaveRentalAmounts",
            new { iUserID = portfolioId, iPropertyID = propertyId, iYear = year, xmlAmounts = RentalAmountsXml.Write(amounts) },
            commandType: CommandType.StoredProcedure), "Success");
    }

    public async Task<ProcResult> SaveAssetAsync(int portfolioId, RentalAsset a)
    {
        await using var con = db.Create();
        var row = await con.QuerySingleAsync<(string Result, int? Id)>("uspSaveRentalAsset",
            new { iUserID = portfolioId, iPropertyID = a.PropertyId, iAssetID = a.Id, strKind = a.Kind, strDescription = a.Description,
                dtmStart = a.Start.ToDateTime(TimeOnly.MinValue), dblCost = a.Cost, dblLifeYears = a.LifeYears, strMethod = a.Method, dblRate = a.Rate,
                dtmDisposed = a.Disposed?.ToDateTime(TimeOnly.MinValue) },
            commandType: CommandType.StoredProcedure);
        return ProcResult.From(row.Result, "Success", row.Id);
    }

    public async Task<ProcResult> DeleteAssetAsync(int portfolioId, int assetId)
    {
        await using var con = db.Create();
        return ProcResult.From(await con.ExecuteScalarAsync<string>("uspDeleteRentalAsset", new { iUserID = portfolioId, iAssetID = assetId },
            commandType: CommandType.StoredProcedure), "Success");
    }
}
