using PortfolioManager.Core.Payouts;
using PortfolioManager.Core.Tax;

namespace PortfolioManager.Core.TaxWork;

/// <summary>Where each amount on a rental payout (agent statement) goes on the rental schedule.</summary>
public static class RentalPayoutMap
{
    /// <summary>The tblPayouts column of each RentalPayoutInput field, and the schedule category it fills.</summary>
    public static readonly IReadOnlyDictionary<string, string> Categories = new Dictionary<string, string>
    {
        ["dblRentalIncomeAmount"] = RentalCategories.GrossRent,
        ["dblOtherRentalRelatedIncomeAmount"] = RentalCategories.OtherIncome,
        ["dblAdvertisingForTenantsExpenseAmount"] = "Advertising",
        ["dblBodyCorportateFeesExpenseAmount"] = "BodyCorporate",
        ["dblBorrowingExpensesAmount"] = "Borrowing",
        ["dblCleaningExpenseAmount"] = "Cleaning",
        ["dblCouncilRatesExpenseAmount"] = "CouncilRates",
        ["dblCapitalAllowancesExpenseAmount"] = RentalCategories.DeclineInValue,
        ["dblGardeningAndLawnMowingExpenseAmount"] = "Gardening",
        ["dblInsuranceExpenseAmount"] = "Insurance",
        ["dblInterestOnLoansExpenseAmount"] = RentalCategories.Interest,
        ["dblLandTaxExpenseAmount"] = "LandTax",
        ["dblLegalFeesExpenseAmount"] = "Legal",
        ["dblPestControlExpenseAmount"] = "PestControl",
        ["dblPropertyAgentFeesAndCommissionExpenseAmount"] = "AgentFees",
        ["dblRepairsAndMaintenanceExpenseAmount"] = RentalCategories.Repairs,
        ["dblCapitalWorksSpecialBuildWriteOffExpenseAmount"] = RentalCategories.CapitalWorksEntered,
        ["dblStationaryTelephoneAndPostageExpenseAmount"] = "Stationery",
        ["dblTravelExpenseAmount"] = RentalCategories.Travel,
        ["dblWaterChargesExpenseAmount"] = "Water",
    };

    /// <summary>The category for a payout column: the ten sundry columns are sundry expenses.</summary>
    public static string CategoryFor(string column) =>
        Categories.TryGetValue(column, out var c) ? c : RentalCategories.Sundry;

    /// <summary>Every rental payout column, in the order RentalPayoutInput lists them.</summary>
    public static IEnumerable<string> Columns => RentalPayoutInput.Fields.Select(f => f.Column);
}

/// <summary>A dated rental amount from a linked record (agent statement, maintenance job, rent payment, tenant bill), for monthly statements.</summary>
public sealed record RentalDatedAmount(int PropertyId, DateOnly Date, string Category, decimal Amount, string Source);

/// <summary>The sale of a rental property, as entered.</summary>
public sealed record RentalSale(int PropertyId, DateOnly Acquired, DateOnly Contract, decimal Proceeds, decimal PurchasePrice, decimal AcquisitionCosts,
    decimal HoldingCosts, decimal Improvements, decimal TitleCosts, int DaysMainResidence, int DaysAbsence, DateOnly? FirstRented,
    decimal? MarketValueFirstRented, string? Note);

/// <summary>
/// The capital gain on a rental property sold, for the whole property and the owner's share.
/// </summary>
public sealed record RentalSaleResult(
    RentalSale Sale,
    decimal CostBase,
    decimal CapitalWorksClaimed,
    decimal ReducedCostBase,
    DateOnly AcquiredForCgt,
    int DaysOwned,
    int DaysExempt,
    decimal TaxableShare,
    decimal WholeGain,
    decimal OwnerPct,
    bool MarketValueRule,
    bool PreCgt,
    bool AbsenceCapped)
{
    /// <summary>The gain (or loss) after the main residence exemption, for the whole property.</summary>
    public decimal TaxableWholeGain => Math.Round(WholeGain * TaxableShare, 2);

    /// <summary>The owner's share of the gain, before losses and the CGT discount.</summary>
    public decimal OwnerGain => Math.Round(TaxableWholeGain * OwnerPct / 100m, 2);

    public bool DiscountEligible => CgtDiscount.Qualifies(AcquiredForCgt, Sale.Contract);

    /// <summary>
    /// The sale as a CGT event for the capital gains report: the owner's share of the proceeds and the reduced cost base
    /// after the main residence exemption. None for a pre-CGT property or one that's fully exempt.
    /// </summary>
    public CgtEvent? Event(string address)
    {
        if (PreCgt || TaxableShare <= 0)
            return null;
        var factor = TaxableShare * OwnerPct / 100m;
        return new CgtEvent("PROP", address, AcquiredForCgt, Sale.Contract, 0m,
            Math.Round(ReducedCostBase * factor, 2), Math.Round(Sale.Proceeds * factor, 2), CgtEventType.PropertySale);
    }
}

public static class RentalSaleCalc
{
    /// <summary>Properties bought before 20 September 1985 are pre-CGT: no gain or loss.</summary>
    public static readonly DateOnly CgtStart = new(1985, 9, 20);

    /// <summary>The longest a rented home can be treated as the main residence while you live elsewhere (6 years).</summary>
    public const int AbsenceLimitDays = 6 * 365 + 1;

    /// <summary>
    /// Cost base: the five elements (element 1 is the market value when the home was first rented, if that rule applies).
    /// Capital works deductions claimed come off it. The main residence days (and up to six years away while rented) are
    /// exempt, pro rata over the days owned (from the date first rented under the market value rule).
    /// </summary>
    public static RentalSaleResult Work(RentalSale s, decimal ownerPct, IEnumerable<RentalAsset> assets)
    {
        var marketValueRule = s.FirstRented is not null && s.MarketValueFirstRented is not null;
        var acquired = marketValueRule ? s.FirstRented!.Value : s.Acquired;
        var element1 = marketValueRule ? s.MarketValueFirstRented!.Value : s.PurchasePrice;
        var costBase = element1 + s.AcquisitionCosts + s.HoldingCosts + s.Improvements + s.TitleCosts;
        var saleYear = FinancialYear.EndYearFor(s.Contract);
        var claimed = assets.Where(a => !a.IsPlant && (!marketValueRule || a.Start >= acquired))
            .Sum(a => RentalDepreciation.ClaimedTo(a with { Disposed = a.Disposed ?? s.Contract }, saleYear));
        var reduced = Math.Max(0, costBase - claimed);
        var days = s.Contract.DayNumber - acquired.DayNumber + 1;
        var absence = Math.Min(s.DaysAbsence, AbsenceLimitDays);
        var exempt = Math.Min(days, s.DaysMainResidence + absence);
        var share = days <= 0 ? 0 : Math.Round((decimal)(days - exempt) / days, 6);
        var preCgt = s.Acquired < CgtStart;
        return new RentalSaleResult(s, costBase, claimed, reduced, acquired, days, exempt, share, preCgt ? 0 : s.Proceeds - reduced,
            ownerPct, marketValueRule, preCgt, s.DaysAbsence > AbsenceLimitDays);
    }
}

/// <summary>A property's value, loans, loan-to-value ratio and yields.</summary>
public sealed record RentalYield(RentalProperty Property, decimal? Value, decimal Loans, decimal GrossRent, decimal CashExpenses)
{
    /// <summary>The owner's share of the value less the loans linked to the property.</summary>
    public decimal Equity => Math.Round((Value ?? 0) * Property.OwnershipPct / 100m, 2) - Loans;

    public decimal? LoanToValue => Value is > 0 ? Math.Round(Loans / (Value.Value * Property.OwnershipPct / 100m), 4) : null;

    /// <summary>A year's rent over the value.</summary>
    public decimal? GrossYield => Value is > 0 ? Math.Round(GrossRent / Value.Value, 4) : null;

    /// <summary>Rent less cash expenses (not interest or depreciation) over the value.</summary>
    public decimal? NetYield => Value is > 0 ? Math.Round((GrossRent - CashExpenses) / Value.Value, 4) : null;
}

/// <summary>Something on a property's schedule worth a second look before the return is lodged.</summary>
public sealed record RentalFlag(int PropertyId, string Address, string Text);

public static class RentalReview
{
    public const decimal LargeRepair = 5_000m;

    /// <summary>
    /// Checks a property's year: interest with no rent, large repairs that may be capital, months with no agent statement
    /// (when the property has an agent statement code), no weeks rented, travel claimed, and shares nobody owns.
    /// </summary>
    public static IReadOnlyList<RentalFlag> Check(RentalPropertyYear y, IReadOnlyCollection<DateOnly> statementDates, IEnumerable<decimal> repairJobs,
        DateOnly today)
    {
        var p = y.Property;
        var flags = new List<RentalFlag>();
        void Flag(string text) => flags.Add(new RentalFlag(p.Id, p.Address, text));

        if (y.WholeInterest > 0 && y.WholeIncome == 0)
            Flag("Interest is claimed but there is no rent. Check the property was genuinely available for rent.");
        if (y.Amount(RentalCategories.Repairs) >= LargeRepair || repairJobs.Any(c => c >= LargeRepair))
            Flag($"Repairs of {LargeRepair:C0} or more. Check none are initial repairs or improvements, which are capital.");
        if (y.WholeIncome > 0 && y.Weeks == 0 && y.Amounts.GetValueOrDefault(RentalCategories.DaysAvailable) == 0)
            Flag("Weeks rented isn't entered.");
        if (y.Amount(RentalCategories.Travel) > 0)
            Flag("Travel is entered. It isn't deductible for residential rental property since 1 July 2017, so it's left out.");
        if (y.PrivateUseFactor < 1)
            Flag($"Expenses are cut to {y.PrivateUseFactor:P0} for private use or days not available for rent.");
        if (p.UnrecordedPct > 0.001m)
            Flag($"Only {100 - p.UnrecordedPct:0.##}% of the property is recorded as owned. Add the other owners to give them their own schedules.");
        if (p.PayoutCode is not null)
            foreach (var month in MissingMonths(y.Year, p.FirstEarned, statementDates, today))
                Flag($"No agent statement for {month:MMMM yyyy}.");
        return flags;
    }

    /// <summary>Months of the year (up to last month) after the property was first rented with no statement dated in them.</summary>
    public static IReadOnlyList<DateOnly> MissingMonths(int year, DateOnly? firstEarned, IReadOnlyCollection<DateOnly> statementDates, DateOnly today)
    {
        var missing = new List<DateOnly>();
        var start = FinancialYear.StartDate(year);
        var end = FinancialYear.EndDate(year);
        for (var m = new DateOnly(start.Year, start.Month, 1); m <= end; m = m.AddMonths(1))
        {
            var monthEnd = m.AddMonths(1).AddDays(-1);
            if (monthEnd >= today || (firstEarned is { } f && monthEnd < f))
                continue;
            if (!statementDates.Any(d => d >= m && d <= monthEnd))
                missing.Add(m);
        }
        return missing;
    }
}

/// <summary>One schedule line in two years, flagged when it changed a lot.</summary>
public sealed record RentalComparisonLine(string Name, decimal Previous, decimal Current, bool Flagged)
{
    public decimal Change => Current - Previous;
    public decimal? ChangePct => Previous != 0 ? Math.Round(Change / Math.Abs(Previous), 4) : null;
}

public static class RentalComparison
{
    /// <summary>A change is flagged when it is at least this much and this share of last year's figure (or new this year).</summary>
    public const decimal FlagAmount = 1_000m, FlagShare = 0.25m;

    public static bool IsBig(decimal previous, decimal current)
    {
        var change = Math.Abs(current - previous);
        return change >= FlagAmount && (previous == 0 || change / Math.Abs(previous) >= FlagShare);
    }

    /// <summary>The whole property's schedule lines in both years, with the net rent at the end.</summary>
    public static IReadOnlyList<RentalComparisonLine> Compare(RentalPropertyYear previous, RentalPropertyYear current)
    {
        var before = previous.Lines().ToDictionary(l => l.Name, l => l.Whole);
        var lines = current.Lines()
            .Where(l => l.Whole != 0 || before.GetValueOrDefault(l.Name) != 0)
            .Select(l => new RentalComparisonLine(l.Name, before.GetValueOrDefault(l.Name), l.Whole, IsBig(before.GetValueOrDefault(l.Name), l.Whole)))
            .ToList();
        lines.Add(new RentalComparisonLine("Net rent (whole property)", previous.WholeNet, current.WholeNet, IsBig(previous.WholeNet, current.WholeNet)));
        return lines;
    }
}

/// <summary>An asset's figures for each year of the depreciation schedule.</summary>
public sealed record DepreciationScheduleRow(RentalAsset Asset, IReadOnlyList<AssetYear> Years);

public static class RentalDepreciationSchedule
{
    /// <summary>Each asset's opening value, deduction and closing value for the year and the years after it (projected).</summary>
    /// <summary>Pooled assets are shown as one low-value pool row, worked out for the pool as a whole.</summary>
    public static IReadOnlyList<DepreciationScheduleRow> Build(IEnumerable<RentalAsset> assets, int fromYear, int years)
    {
        var list = assets.ToList();
        var rows = list.Where(a => !a.IsPooled)
            .Select(a => new DepreciationScheduleRow(a, Enumerable.Range(fromYear, years).Select(y => RentalDepreciation.For(a, y)).ToList()))
            .Where(r => r.Years.Any(y => y.Opening > 0))
            .ToList();
        if (list.FirstOrDefault(a => a.IsPooled) is { } first)
        {
            var poolYears = Enumerable.Range(fromYear, years).Select(y =>
                    RentalLowValuePool.Line(RentalLowValuePool.For(list, y), first.PropertyId, Tax.FinancialYear.StartDate(y))
                    ?? new AssetYear(first, 0, 0, 0))
                .ToList();
            if (poolYears.Any(y => y.Opening > 0))
                rows.Add(new DepreciationScheduleRow(poolYears.First(y => y.Asset.Id == 0 || y.Opening > 0).Asset with { Description = "Low-value pool", Cost = list.Where(a => a.IsPooled).Sum(a => a.Cost) }, poolYears));
        }
        return rows;
    }
}

/// <summary>An income and expense statement for a property by month: the dated amounts in each month and the year's keyed figures.</summary>
public sealed record RentalStatement(IReadOnlyList<DateOnly> Months, IReadOnlyList<RentalStatementLine> Lines)
{
    public decimal MonthNet(DateOnly month) => Lines.Sum(l => l.Sign * l.ByMonth.GetValueOrDefault(month));
    public decimal KeyedNet => Lines.Sum(l => l.Sign * l.Keyed);
    public decimal Net => Lines.Sum(l => l.Sign * l.Total);
}

public sealed record RentalStatementLine(string Category, string Name, bool IsIncome, IReadOnlyDictionary<DateOnly, decimal> ByMonth, decimal Keyed)
{
    public int Sign => IsIncome ? 1 : -1;
    public decimal Total => ByMonth.Values.Sum() + Keyed;
}

public static class RentalStatements
{
    /// <summary>
    /// Dated amounts (agent statements, repairs, rent paid, tenant bills) by month for the period, plus the figures keyed for
    /// the year when the period is a whole financial year (they have no dates, so they get their own column).
    /// </summary>
    public static RentalStatement Build(DateOnly from, DateOnly to, IEnumerable<RentalDatedAmount> dated, IReadOnlyDictionary<string, decimal>? keyed)
    {
        var months = new List<DateOnly>();
        for (var m = new DateOnly(from.Year, from.Month, 1); m <= to; m = m.AddMonths(1))
            months.Add(m);
        var inPeriod = dated.Where(d => d.Date >= from && d.Date <= to).ToList();
        var lines = RentalCategories.All
            .Select(c => new RentalStatementLine(c.Key, c.Name, c.Label == RentalLabel.Income,
                inPeriod.Where(d => d.Category == c.Key).GroupBy(d => new DateOnly(d.Date.Year, d.Date.Month, 1)).ToDictionary(g => g.Key, g => g.Sum(d => d.Amount)),
                keyed?.GetValueOrDefault(c.Key) ?? 0))
            .Where(l => l.Total != 0 || l.ByMonth.Count > 0)
            .ToList();
        return new RentalStatement(months, lines);
    }
}
