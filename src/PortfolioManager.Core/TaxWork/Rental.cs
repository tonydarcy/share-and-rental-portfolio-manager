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
    public const string GrossRent = "GrossRent", OtherIncome = "OtherIncome", Interest = "Interest", Weeks = "Weeks", Repairs = "Repairs",
        DeclineInValue = "DeclineInValue", CapitalWorksEntered = "CapitalWorksEntered", Travel = "Travel", Sundry = "Sundry";

    /// <summary>Days kept with the year's amounts for a holiday home or a property also used privately (not money).</summary>
    public const string DaysAvailable = "DaysAvailable", DaysPrivate = "DaysPrivate", DaysOwned = "DaysOwned";

    /// <summary>Keys kept with the amounts that are counts, not money.</summary>
    public static readonly IReadOnlyList<string> Counts = [Weeks, DaysAvailable, DaysPrivate, DaysOwned];

    public static readonly IReadOnlyList<RentalCategory> Income =
    [
        new(GrossRent, "Gross rent", RentalLabel.Income),
        new(OtherIncome, "Other rental-related income", RentalLabel.Income, "Insurance payouts for lost rent, bond money kept, letting fees"),
    ];

    /// <summary>In the order of the ATO rental property schedule.</summary>
    public static readonly IReadOnlyList<RentalCategory> Expenses =
    [
        new("Advertising", "Advertising for tenants", RentalLabel.Other),
        new("BodyCorporate", "Body corporate fees and charges", RentalLabel.Other, "Not special levies for capital works"),
        new("Borrowing", "Borrowing expenses", RentalLabel.Other, "This year's share: costs over $100 are spread over 5 years or the loan term"),
        new("Cleaning", "Cleaning", RentalLabel.Other),
        new("CouncilRates", "Council rates", RentalLabel.Other),
        new(DeclineInValue, "Capital allowances entered", RentalLabel.Other, "Only decline in value not worked out from the assets below, e.g. from an agent statement"),
        new("Gardening", "Gardening and lawn mowing", RentalLabel.Other),
        new("Insurance", "Insurance", RentalLabel.Other),
        new(Interest, "Interest on loans", RentalLabel.Interest, "Only the part of the loan used for the property. Loans linked to the property replace this figure"),
        new("LandTax", "Land tax", RentalLabel.Other),
        new("Legal", "Legal fees", RentalLabel.Other, "Revenue legal costs only, such as evicting a tenant"),
        new("PestControl", "Pest control", RentalLabel.Other),
        new("AgentFees", "Property agent fees and commission", RentalLabel.Other),
        new(Repairs, "Repairs and maintenance", RentalLabel.Other, "Not initial repairs or improvements, which are capital"),
        new(CapitalWorksEntered, "Capital works entered", RentalLabel.CapitalWorks, "Only capital works not worked out from the assets below"),
        new("Stationery", "Stationery, telephone and postage", RentalLabel.Other),
        new(Travel, "Travel", RentalLabel.None, "Not deductible for residential rental property since 1 July 2017 (kept for the record only)"),
        new("Water", "Water charges", RentalLabel.Other),
        new(Sundry, "Sundry rental expenses", RentalLabel.Other),
    ];

    public static readonly IReadOnlyList<RentalCategory> All = [.. Income, .. Expenses];

    public static RentalCategory? Find(string key) => All.FirstOrDefault(c => c.Key == key);

    public static bool IsExpense(string key) => Expenses.Any(c => c.Key == key);
}

public static class RentalAssetKinds
{
    public const string Plant = "Plant", Building = "Building";
    public const string DiminishingValue = "DV", PrimeCost = "PC", LowValuePool = "LV";

    public static string MethodText(string? method) => method switch
    {
        DiminishingValue => "Diminishing value",
        PrimeCost => "Prime cost",
        LowValuePool => "Low-value pool",
        _ => "",
    };
}

/// <summary>Another owner of a property, who gets their own schedule.</summary>
public sealed record RentalCoOwner(int Id, string Name, decimal Pct);

/// <summary>
/// A rental property. OwnershipPct is the portfolio owner's share. PayoutCode is the property code used on rental payouts
/// (agent statements), which then fill the schedule.
/// </summary>
public sealed record RentalProperty(int Id, string Address, DateOnly? FirstEarned, decimal OwnershipPct, bool Active, string? Note,
    string? PayoutCode = null, string? State = null, decimal? Value = null, DateOnly? Valued = null, IReadOnlyList<RentalCoOwner>? CoOwners = null)
{
    public IReadOnlyList<RentalCoOwner> Others => CoOwners ?? [];

    /// <summary>The share nobody is recorded as owning (more than nil when co-owners haven't been entered).</summary>
    public decimal UnrecordedPct => Math.Max(0, 100 - OwnershipPct - Others.Sum(o => o.Pct));
}

public sealed record RentalAsset(int Id, int PropertyId, string Kind, string Description, DateOnly Start, decimal Cost, decimal? LifeYears,
    string? Method, decimal? Rate, DateOnly? Disposed, decimal? TerminationValue = null)
{
    public bool IsPlant => Kind == RentalAssetKinds.Plant;
    public bool IsPooled => IsPlant && Method == RentalAssetKinds.LowValuePool;

    public string HowText => IsPlant
        ? IsPooled ? "Low-value pool (18.75% then 37.5%)" : $"{RentalAssetKinds.MethodText(Method)}, {LifeYears:0.##} years"
        : $"{Rate:0.##}% a year";
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
    /// <summary>The low-value pool rates: 18.75% in the year an asset is added, 37.5% of the opening balance after that.</summary>
    public const decimal PoolFirstYearRate = 0.1875m, PoolRate = 0.375m;

    /// <summary>Days the asset is held in the year: from the later of its start and the year start to the earlier of its disposal and the year end.</summary>
    public static int DaysHeld(RentalAsset a, DateOnly yearStart, DateOnly yearEnd)
    {
        var from = a.Start > yearStart ? a.Start : yearStart;
        var to = a.Disposed is { } d && d < yearEnd ? d : yearEnd;
        return to < from ? 0 : to.DayNumber - from.DayNumber + 1;
    }

    /// <summary>
    /// Diminishing value: opening value × days/365 × 200% / effective life. Prime cost: cost × days/365 × 100% / effective life.
    /// Capital works: cost × rate × days held / days in the income year (366 in a leap year, s 43-210). Low-value pool: 18.75% of the cost in the first year, then 37.5% of the opening
    /// value, whatever the days. None goes below nil.
    /// </summary>
    public static decimal YearDeduction(RentalAsset a, decimal opening, int days, bool firstYear = false, int daysInYear = 365)
    {
        if (days <= 0 || opening <= 0)
            return 0;
        var amount = a.Kind switch
        {
            RentalAssetKinds.Plant when a.IsPooled => firstYear ? a.Cost * PoolFirstYearRate : opening * PoolRate,
            RentalAssetKinds.Plant when a.LifeYears is > 0 && a.Method == RentalAssetKinds.DiminishingValue =>
                Assets.DepreciationEngine.Diminishing(opening, days, a.LifeYears.Value),
            RentalAssetKinds.Plant when a.LifeYears is > 0 => Assets.DepreciationEngine.Prime(a.Cost, days, a.LifeYears.Value),
            RentalAssetKinds.Building when a.Rate is > 0 => a.Cost * days / (decimal)daysInYear * a.Rate.Value / 100m,
            _ => 0,
        };
        return Math.Min(Math.Round(amount, 2), opening);
    }

    /// <summary>The asset's figures for the year ending endYear, after every earlier year's deduction.</summary>
    public static AssetYear For(RentalAsset a, int endYear, YearStart? start = null)
    {
        var firstYear = FinancialYear.EndYearFor(a.Start, start);
        if (endYear < firstYear)
            return new AssetYear(a, 0, 0, 0);
        var opening = a.Cost;
        for (var y = firstYear; y < endYear; y++)
            opening -= YearDeduction(a, opening, DaysHeld(a, FinancialYear.StartDate(y, start), FinancialYear.EndDate(y, start)), y == firstYear, DaysIn(y, start));
        var days = DaysHeld(a, FinancialYear.StartDate(endYear, start), FinancialYear.EndDate(endYear, start));
        return new AssetYear(a, opening, days, YearDeduction(a, opening, days, endYear == firstYear, DaysIn(endYear, start)));
    }

    private static int DaysIn(int endYear, YearStart? start) =>
        FinancialYear.EndDate(endYear, start).DayNumber - FinancialYear.StartDate(endYear, start).DayNumber + 1;

    /// <summary>All the deductions claimed on the asset up to and including the year ending endYear.</summary>
    public static decimal ClaimedTo(RentalAsset a, int endYear, YearStart? start = null) =>
        endYear < FinancialYear.EndYearFor(a.Start, start) ? 0 : a.Cost - For(a, endYear, start).Closing;

    /// <summary>
    /// The balancing adjustment when a depreciating asset is sold or scrapped in the year: the termination value less its
    /// value at that date. More than nil is assessable, less than nil is a deduction. Nil when it wasn't disposed of in the year.
    /// </summary>
    public static decimal BalancingAdjustment(RentalAsset a, int endYear, YearStart? start = null)
    {
        if (!a.IsPlant || a.Disposed is not { } d || FinancialYear.EndYearFor(d, start) != endYear)
            return 0;
        return Math.Round((a.TerminationValue ?? 0) - For(a, endYear, start).Closing, 2);
    }
}

/// <summary>A holiday home or a property also used privately: the share of the expenses that can be claimed.</summary>
public static class RentalPrivateUse
{
    /// <summary>
    /// Days rented or genuinely available for rent, over the days owned in the year (the year's length when not entered).
    /// 1 when no days are entered, so a property rented all year needs nothing here.
    /// </summary>
    public static decimal Factor(IReadOnlyDictionary<string, decimal> amounts, int year, YearStart? start = null)
    {
        var available = amounts.GetValueOrDefault(RentalCategories.DaysAvailable);
        var personal = amounts.GetValueOrDefault(RentalCategories.DaysPrivate);
        if (available <= 0 && personal <= 0)
            return 1;
        var owned = amounts.GetValueOrDefault(RentalCategories.DaysOwned) is > 0 and var o
            ? o
            : FinancialYear.EndDate(year, start).DayNumber - FinancialYear.StartDate(year, start).DayNumber + 1;
        return Math.Clamp(Math.Round(available / owned, 6), 0, 1);
    }
}

/// <summary>One line of the ATO rental property schedule: the whole property's figure and the owner's share.</summary>
public sealed record RentalScheduleLine(string Name, decimal Whole, decimal Share, RentalLabel Label);

/// <summary>
/// A property's schedule for a year: the whole property's figures and the owner's share. Amounts are what was keyed on the
/// Rental properties page; Linked are filled in from agent statements, the maintenance log, the rent ledger and bills
/// passed on to the tenant (negative). LoanInterest, when loans are linked, replaces the interest figures.
/// Expenses are cut to the private use factor; income isn't.
/// </summary>
public sealed record RentalPropertyYear(RentalProperty Property, int Year, IReadOnlyDictionary<string, decimal> Amounts, IReadOnlyList<AssetYear> Assets,
    IReadOnlyDictionary<string, decimal>? Linked = null, decimal? LoanInterest = null, decimal BalancingAdjustments = 0)
{
    private decimal Share(decimal whole) => Math.Round(whole * Property.OwnershipPct / 100m, 2);

    private decimal Use(decimal whole) => PrivateUseFactor == 1 ? whole : Math.Round(whole * PrivateUseFactor, 2);

    public decimal Weeks => Amounts.GetValueOrDefault(RentalCategories.Weeks);

    public decimal PrivateUseFactor => RentalPrivateUse.Factor(Amounts, Year);

    /// <summary>What was keyed plus what came from the linked records, before any private use.</summary>
    public decimal Amount(string key) => Amounts.GetValueOrDefault(key) + (Linked?.GetValueOrDefault(key) ?? 0);

    public decimal LinkedAmount(string key) => Linked?.GetValueOrDefault(key) ?? 0;

    public decimal BalancingIncome => Math.Max(0, BalancingAdjustments);
    public decimal BalancingDeduction => Math.Max(0, -BalancingAdjustments);

    public decimal CapitalAllowances => Assets.Where(a => a.Asset.IsPlant).Sum(a => a.Deduction) + BalancingDeduction;
    public decimal CapitalWorks => Assets.Where(a => !a.Asset.IsPlant).Sum(a => a.Deduction);

    private decimal Keyed(RentalLabel label) =>
        label == RentalLabel.Interest && LoanInterest is { } loans
            ? loans
            : RentalCategories.All.Where(c => c.Label == label).Sum(c => Amount(c.Key));

    public decimal WholeIncome => Keyed(RentalLabel.Income) + BalancingIncome;
    public decimal WholeInterest => Use(Keyed(RentalLabel.Interest));
    public decimal WholeCapitalWorks => Use(Keyed(RentalLabel.CapitalWorks) + CapitalWorks);
    public decimal WholeOther => Use(Keyed(RentalLabel.Other) + CapitalAllowances);
    public decimal WholeNet => WholeIncome - WholeInterest - WholeCapitalWorks - WholeOther;

    public decimal GrossRent => Share(WholeIncome);
    public decimal Interest => Share(WholeInterest);
    public decimal CapitalWorksShare => Share(WholeCapitalWorks);
    public decimal OtherDeductions => Share(WholeOther);
    public decimal Net => GrossRent - Interest - CapitalWorksShare - OtherDeductions;

    /// <summary>Cash expenses (not interest, depreciation or capital works), for the net yield.</summary>
    public decimal WholeCashExpenses => Use(RentalCategories.Expenses.Where(c => c.Label == RentalLabel.Other && c.Key != RentalCategories.DeclineInValue).Sum(c => Amount(c.Key)));

    /// <summary>The lines of the ATO rental property schedule, in its order, with the whole property and the owner's share.</summary>
    public IReadOnlyList<RentalScheduleLine> Lines()
    {
        var lines = new List<RentalScheduleLine>();
        void Add(string name, decimal whole, RentalLabel label) => lines.Add(new RentalScheduleLine(name, whole, Share(whole), label));
        Add("Gross rent", Amount(RentalCategories.GrossRent), RentalLabel.Income);
        Add("Other rental-related income", Amount(RentalCategories.OtherIncome) + BalancingIncome, RentalLabel.Income);
        foreach (var c in RentalCategories.Expenses)
        {
            if (c.Key == RentalCategories.DeclineInValue)
            {
                Add("Capital allowances (depreciation on plant)", Use(Amount(c.Key) + CapitalAllowances), RentalLabel.Other);
                continue;
            }
            if (c.Key == RentalCategories.CapitalWorksEntered)
            {
                Add("Capital works deductions", Use(Amount(c.Key) + CapitalWorks), RentalLabel.CapitalWorks);
                continue;
            }
            var whole = c.Key == RentalCategories.Interest && LoanInterest is { } loans ? loans : Amount(c.Key);
            Add(c.Name, c.Label == RentalLabel.None ? whole : Use(whole), c.Label);
        }
        return lines;
    }

    /// <summary>The same schedule for another owner's share (co-owners each get their own).</summary>
    public RentalPropertyYear ForShare(decimal pct) => this with { Property = Property with { OwnershipPct = pct } };
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

/// <summary>Rental properties (2026-10-21-19-rental-properties.sql and 2026-10-23-01-rental-reports.sql).</summary>
public sealed class RentalService(IDbConnectionFactory db)
{
    public async Task<IReadOnlyList<RentalProperty>> GetPropertiesAsync(int portfolioId)
    {
        await using var con = db.Create();
        try
        {
            var rows = await con.QueryAsync("""
                SELECT iPropertyID, strAddress, dtmFirstEarned, dblOwnershipPct, bActive, strNote, strPayoutCode, strState, dblValue, dtmValued
                FROM tblRentalProperties WHERE iUserID = @portfolioId ORDER BY bActive DESC, strAddress
                """, new { portfolioId });
            var owners = (await con.QueryAsync("""
                SELECT O.iOwnerID, O.iPropertyID, O.strName, O.dblPct FROM tblRentalOwners O
                JOIN tblRentalProperties P ON P.iPropertyID = O.iPropertyID WHERE P.iUserID = @portfolioId ORDER BY O.strName
                """, new { portfolioId })).Cast<IDictionary<string, object?>>()
                .GroupBy(r => Db.Int(r, "iPropertyID") ?? 0)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<RentalCoOwner>)g.Select(r => new RentalCoOwner(Db.Int(r, "iOwnerID") ?? 0, Db.Str(r, "strName"), Db.Dec(r, "dblPct") ?? 0)).ToList());
            return rows.Cast<IDictionary<string, object?>>().Select(r => new RentalProperty(Db.Int(r, "iPropertyID") ?? 0, Db.Str(r, "strAddress"),
                Db.Date(r, "dtmFirstEarned"), Db.Dec(r, "dblOwnershipPct") ?? 100, Db.Bool(r, "bActive") ?? true, Text(Db.Str(r, "strNote")),
                Text(Db.Str(r, "strPayoutCode")), Text(Db.Str(r, "strState")), Db.Dec(r, "dblValue"), Db.Date(r, "dtmValued"),
                owners.GetValueOrDefault(Db.Int(r, "iPropertyID") ?? 0))).ToList();
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 207 or 208)
        {
            return [];
        }
    }

    public async Task<RentalProperty?> GetPropertyAsync(int portfolioId, int propertyId) =>
        (await GetPropertiesAsync(portfolioId)).FirstOrDefault(p => p.Id == propertyId);

    /// <summary>Every property's schedule for the year; properties marked inactive are included only when they have figures.</summary>
    public async Task<RentalSchedule> GetYearAsync(int portfolioId, int year, bool allProperties = false)
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
        var linked = (await GetDatedAsync(portfolioId, properties, FinancialYear.StartDate(year), FinancialYear.EndDate(year)))
            .GroupBy(d => d.PropertyId)
            .ToDictionary(g => g.Key, g => (IReadOnlyDictionary<string, decimal>)g.GroupBy(d => d.Category).ToDictionary(c => c.Key, c => c.Sum(d => d.Amount)));
        var loans = await GetLoanInterestAsync(portfolioId, year);
        var list = properties.Select(p =>
            {
                var mine = assets.GetValueOrDefault(p.Id) ?? [];
                return new RentalPropertyYear(p, year, amounts.GetValueOrDefault(p.Id) ?? new Dictionary<string, decimal>(),
                    mine.Select(a => RentalDepreciation.For(a, year)).Where(a => a.Opening > 0 || a.Deduction > 0).ToList(),
                    linked.GetValueOrDefault(p.Id), loans.TryGetValue(p.Id, out var interest) ? interest : null,
                    mine.Sum(a => RentalDepreciation.BalancingAdjustment(a, year)));
            })
            .Where(p => allProperties || p.Property.Active || p.Amounts.Count > 0 || p.Linked is { Count: > 0 } || p.Assets.Any(a => a.Deduction > 0))
            .ToList();
        return new RentalSchedule(year, list);
    }

    public async Task<IReadOnlyList<RentalAsset>> GetAssetsAsync(int portfolioId)
    {
        await using var con = db.Create();
        return await GetAssetsAsync(con, portfolioId);
    }

    private static async Task<IReadOnlyList<RentalAsset>> GetAssetsAsync(System.Data.Common.DbConnection con, int portfolioId)
    {
        var rows = await con.QueryAsync("""
            SELECT A.iAssetID, A.iPropertyID, A.strKind, A.strDescription, A.dtmStart, A.dblCost, A.dblLifeYears, A.strMethod, A.dblRate, A.dtmDisposed,
                   A.dblTerminationValue
            FROM tblRentalAssets A JOIN tblRentalProperties P ON P.iPropertyID = A.iPropertyID
            WHERE P.iUserID = @portfolioId ORDER BY A.dtmStart, A.iAssetID
            """, new { portfolioId });
        return rows.Cast<IDictionary<string, object?>>().Select(r => new RentalAsset(Db.Int(r, "iAssetID") ?? 0, Db.Int(r, "iPropertyID") ?? 0,
            Db.Str(r, "strKind"), Db.Str(r, "strDescription"), Db.Date(r, "dtmStart") ?? default, Db.Dec(r, "dblCost") ?? 0, Db.Dec(r, "dblLifeYears"),
            Text(Db.Str(r, "strMethod")), Db.Dec(r, "dblRate"), Db.Date(r, "dtmDisposed"), Db.Dec(r, "dblTerminationValue"))).ToList();
    }

    /// <summary>
    /// Dated amounts for the properties between the dates: agent statements (rental payouts with the property's code),
    /// repairs from the maintenance log, rent paid in the rent ledger (only for properties without an agent statement
    /// code, whose rent the statements already carry) and bills the tenant paid (netted off the expense, so negative).
    /// </summary>
    public async Task<IReadOnlyList<RentalDatedAmount>> GetDatedAsync(int portfolioId, IReadOnlyList<RentalProperty> properties, DateOnly from, DateOnly to)
    {
        var list = new List<RentalDatedAmount>();
        await using var con = db.Create();
        var args = new { portfolioId, from = from.ToDateTime(TimeOnly.MinValue), to = to.ToDateTime(TimeOnly.MinValue) };
        var byCode = properties.Where(p => p.PayoutCode is not null).GroupBy(p => p.PayoutCode!.ToUpperInvariant()).ToDictionary(g => g.Key, g => g.First().Id);
        if (byCode.Count > 0)
        {
            // Column names come from the fixed list in RentalPayoutInput, never from input
            var rows = await con.QueryAsync("SELECT UPPER(LTRIM(RTRIM(strStockCode))) AS Code, dtmTransactionDate, " + string.Join(", ", RentalPayoutMap.Columns) +
                " FROM tblPayouts WHERE iUserID = @portfolioId AND dtmTransactionDate >= @from AND dtmTransactionDate < DATEADD(day, 1, @to) AND UPPER(LTRIM(RTRIM(strStockCode))) IN @codes",
                new { args.portfolioId, args.from, args.to, codes = byCode.Keys.ToArray() });
            foreach (var r in rows.Cast<IDictionary<string, object?>>())
            {
                var id = byCode[Db.Str(r, "Code")];
                var date = Db.Date(r, "dtmTransactionDate") ?? from;
                foreach (var column in RentalPayoutMap.Columns)
                    if (Db.Dec(r, column) is { } v && v != 0)
                        list.Add(new RentalDatedAmount(id, date, RentalPayoutMap.CategoryFor(column), Math.Round(v, 2), "Agent statement"));
            }
        }
        await IfMigratedAsync(async () =>
        {
            foreach (var r in (await con.QueryAsync("""
                SELECT J.iPropertyID, J.dtmDone, J.dblCost FROM tblRentalJobs J JOIN tblRentalProperties P ON P.iPropertyID = J.iPropertyID
                WHERE P.iUserID = @portfolioId AND J.strTreatment = 'Repair' AND J.dtmDone BETWEEN @from AND @to
                """, args)).Cast<IDictionary<string, object?>>())
                list.Add(new RentalDatedAmount(Db.Int(r, "iPropertyID") ?? 0, Db.Date(r, "dtmDone") ?? from, RentalCategories.Repairs, Db.Dec(r, "dblCost") ?? 0, "Maintenance log"));
            foreach (var r in (await con.QueryAsync("""
                SELECT B.iPropertyID, B.dtmPaid, B.strCategory, B.dblAmount FROM tblRentalTenantBills B JOIN tblRentalProperties P ON P.iPropertyID = B.iPropertyID
                WHERE P.iUserID = @portfolioId AND B.dtmPaid BETWEEN @from AND @to
                """, args)).Cast<IDictionary<string, object?>>())
                list.Add(new RentalDatedAmount(Db.Int(r, "iPropertyID") ?? 0, Db.Date(r, "dtmPaid") ?? from, Db.Str(r, "strCategory"), -(Db.Dec(r, "dblAmount") ?? 0), "Paid by the tenant"));
            var withoutCode = properties.Where(p => p.PayoutCode is null).Select(p => p.Id).ToArray();
            if (withoutCode.Length > 0)
                foreach (var r in (await con.QueryAsync("""
                    SELECT T.iPropertyID, R.dtmPaid, R.dblAmount FROM tblRentalRentPayments R JOIN tblRentalTenancies T ON T.iTenancyID = R.iTenancyID
                    WHERE T.iPropertyID IN @withoutCode AND R.dtmPaid BETWEEN @from AND @to
                    """, new { withoutCode, args.from, args.to })).Cast<IDictionary<string, object?>>())
                    list.Add(new RentalDatedAmount(Db.Int(r, "iPropertyID") ?? 0, Db.Date(r, "dtmPaid") ?? from, RentalCategories.GrossRent, Db.Dec(r, "dblAmount") ?? 0, "Rent ledger"));
        });
        return list;
    }

    /// <summary>The deductible interest for the year of the loans linked to each property (only properties with linked loans).</summary>
    public async Task<IReadOnlyDictionary<int, decimal>> GetLoanInterestAsync(int portfolioId, int year)
    {
        var links = await GetLoanLinksAsync(portfolioId);
        if (links.Count == 0)
            return new Dictionary<int, decimal>();
        var (loans, entries) = await new Debt.LoanService(db).GetAsync(portfolioId);
        var from = FinancialYear.StartDate(year);
        var to = FinancialYear.EndDate(year);
        return loans.Where(l => links.ContainsKey(l.Id))
            .GroupBy(l => links[l.Id])
            .ToDictionary(g => g.Key, g => g.Sum(l => Debt.LoanCalc.Year(l, entries, from, to).Deductible));
    }

    /// <summary>Which property each linked loan is for (loan id to property id).</summary>
    public async Task<IReadOnlyDictionary<int, int>> GetLoanLinksAsync(int portfolioId)
    {
        await using var con = db.Create();
        try
        {
            return (await con.QueryAsync<(int LoanId, int PropertyId)>("""
                SELECT L.iLoanID, L.iPropertyID FROM tblRentalLoanLinks L JOIN tblRentalProperties P ON P.iPropertyID = L.iPropertyID WHERE P.iUserID = @portfolioId
                """, new { portfolioId })).ToDictionary(r => r.LoanId, r => r.PropertyId);
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208)
        {
            return new Dictionary<int, int>();
        }
    }

    public async Task<IReadOnlyList<RentalSale>> GetSalesAsync(int portfolioId)
    {
        await using var con = db.Create();
        try
        {
            var rows = await con.QueryAsync("""
                SELECT S.* FROM tblRentalSales S JOIN tblRentalProperties P ON P.iPropertyID = S.iPropertyID WHERE P.iUserID = @portfolioId
                """, new { portfolioId });
            return rows.Cast<IDictionary<string, object?>>().Select(r => new RentalSale(Db.Int(r, "iPropertyID") ?? 0, Db.Date(r, "dtmAcquired") ?? default,
                Db.Date(r, "dtmContract") ?? default, Db.Dec(r, "dblProceeds") ?? 0, Db.Dec(r, "dblPurchasePrice") ?? 0, Db.Dec(r, "dblAcquisitionCosts") ?? 0,
                Db.Dec(r, "dblHoldingCosts") ?? 0, Db.Dec(r, "dblImprovements") ?? 0, Db.Dec(r, "dblTitleCosts") ?? 0, Db.Int(r, "iDaysMainResidence") ?? 0,
                Db.Int(r, "iDaysAbsence") ?? 0, Db.Date(r, "dtmFirstRented"), Db.Dec(r, "dblMarketValueFirstRented"), Text(Db.Str(r, "strNote")))).ToList();
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208)
        {
            return [];
        }
    }

    /// <summary>Each sold property's gain, worked out with its capital works.</summary>
    public async Task<IReadOnlyList<(RentalProperty Property, RentalSaleResult Result)>> GetSaleResultsAsync(int portfolioId)
    {
        var sales = await GetSalesAsync(portfolioId);
        if (sales.Count == 0)
            return [];
        var properties = (await GetPropertiesAsync(portfolioId)).ToDictionary(p => p.Id);
        var assets = await GetAssetsAsync(portfolioId);
        return sales.Where(s => properties.ContainsKey(s.PropertyId))
            .Select(s => (properties[s.PropertyId], RentalSaleCalc.Work(s, properties[s.PropertyId].OwnershipPct, assets.Where(a => a.PropertyId == s.PropertyId))))
            .ToList();
    }

    /// <summary>The CGT events of the properties sold, for the capital gains report.</summary>
    public async Task<IReadOnlyList<CgtEvent>> GetSaleEventsAsync(int portfolioId) =>
        (await GetSaleResultsAsync(portfolioId)).Select(r => r.Result.Event(r.Property.Address)).OfType<CgtEvent>().ToList();

    public async Task<ProcResult> SavePropertyAsync(int portfolioId, int id, string? address, DateOnly? firstEarned, decimal ownershipPct, bool active, string? note)
    {
        await using var con = db.Create();
        var row = await con.QuerySingleAsync<(string Result, int? Id)>("uspSaveRentalProperty",
            new { iUserID = portfolioId, iPropertyID = id, strAddress = address, dtmFirstEarned = firstEarned?.ToDateTime(TimeOnly.MinValue),
                dblOwnershipPct = ownershipPct, bActive = active, strNote = note },
            commandType: CommandType.StoredProcedure);
        return ProcResult.From(row.Result, "Success", row.Id);
    }

    public Task<ProcResult> SaveDetailsAsync(int portfolioId, int id, string? payoutCode, string? state, decimal? value, DateOnly? valued) =>
        ResultAsync("uspSaveRentalPropertyDetails", new { iUserID = portfolioId, iPropertyID = id, strPayoutCode = payoutCode, strState = state,
            dblValue = value, dtmValued = valued?.ToDateTime(TimeOnly.MinValue) });

    public Task<ProcResult> DeletePropertyAsync(int portfolioId, int id) =>
        ResultAsync("uspDeleteRentalProperty", new { iUserID = portfolioId, iPropertyID = id });

    public Task<ProcResult> SaveAmountsAsync(int portfolioId, int propertyId, int year, IReadOnlyDictionary<string, decimal> amounts) =>
        ResultAsync("uspSaveRentalAmounts", new { iUserID = portfolioId, iPropertyID = propertyId, iYear = year, xmlAmounts = RentalAmountsXml.Write(amounts) });

    public async Task<ProcResult> SaveAssetAsync(int portfolioId, RentalAsset a)
    {
        await using var con = db.Create();
        var row = await con.QuerySingleAsync<(string Result, int? Id)>("uspSaveRentalAsset",
            new { iUserID = portfolioId, iPropertyID = a.PropertyId, iAssetID = a.Id, strKind = a.Kind, strDescription = a.Description,
                dtmStart = a.Start.ToDateTime(TimeOnly.MinValue), dblCost = a.Cost, dblLifeYears = a.LifeYears, strMethod = a.Method, dblRate = a.Rate,
                dtmDisposed = a.Disposed?.ToDateTime(TimeOnly.MinValue), dblTerminationValue = a.TerminationValue },
            commandType: CommandType.StoredProcedure);
        return ProcResult.From(row.Result, "Success", row.Id);
    }

    public Task<ProcResult> DeleteAssetAsync(int portfolioId, int assetId) =>
        ResultAsync("uspDeleteRentalAsset", new { iUserID = portfolioId, iAssetID = assetId });

    public Task<ProcResult> AddOwnerAsync(int portfolioId, int propertyId, string? name, decimal pct) =>
        ResultAsync("uspSaveRentalOwner", new { iUserID = portfolioId, iPropertyID = propertyId, strName = name, dblPct = pct });

    public Task<ProcResult> DeleteOwnerAsync(int portfolioId, int ownerId) =>
        ResultAsync("uspDeleteRentalOwner", new { iUserID = portfolioId, iOwnerID = ownerId });

    /// <summary>Links the loan to the property, or with propertyId 0 unlinks it.</summary>
    public Task<ProcResult> LinkLoanAsync(int portfolioId, int loanId, int propertyId) =>
        ResultAsync("uspLinkRentalLoan", new { iUserID = portfolioId, iLoanID = loanId, iPropertyID = propertyId });

    public Task<ProcResult> SaveSaleAsync(int portfolioId, RentalSale s) =>
        ResultAsync("uspSaveRentalSale", new
        {
            iUserID = portfolioId, iPropertyID = s.PropertyId, dtmAcquired = s.Acquired.ToDateTime(TimeOnly.MinValue), dtmContract = s.Contract.ToDateTime(TimeOnly.MinValue),
            dblProceeds = s.Proceeds, dblPurchasePrice = s.PurchasePrice, dblAcquisitionCosts = s.AcquisitionCosts, dblHoldingCosts = s.HoldingCosts,
            dblImprovements = s.Improvements, dblTitleCosts = s.TitleCosts, iDaysMainResidence = s.DaysMainResidence, iDaysAbsence = s.DaysAbsence,
            dtmFirstRented = s.FirstRented?.ToDateTime(TimeOnly.MinValue), dblMarketValueFirstRented = s.MarketValueFirstRented, strNote = s.Note,
        });

    public Task<ProcResult> DeleteSaleAsync(int portfolioId, int propertyId) =>
        ResultAsync("uspDeleteRentalSale", new { iUserID = portfolioId, iPropertyID = propertyId });

    private async Task<ProcResult> ResultAsync(string proc, object args)
    {
        await using var con = db.Create();
        return ProcResult.From(await con.ExecuteScalarAsync<string>(proc, args, commandType: CommandType.StoredProcedure), "Success");
    }

    /// <summary>Runs a read of the tables added by 2026-10-23-02-running-a-property.sql, skipping it until that has been run.</summary>
    private static async Task IfMigratedAsync(Func<Task> read)
    {
        try
        {
            await read();
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208)
        {
        }
    }

    private static string? Text(string s) => s.Length > 0 ? s : null;
}
