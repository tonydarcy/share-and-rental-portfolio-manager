using PortfolioManager.Core.TaxWork;

namespace PortfolioManager.Core.Rentals;

public static class RentFrequencies
{
    public const string Week = "Week", Fortnight = "Fortnight", Month = "Month";
    public static readonly IReadOnlyList<string> All = [Week, Fortnight, Month];

    public static string Text(string f) => f switch { Week => "a week", Fortnight => "a fortnight", _ => "a month" };

    /// <summary>The next due date after this one.</summary>
    public static DateOnly Next(string frequency, DateOnly start, int n) => frequency switch
    {
        Week => start.AddDays(7 * n),
        Fortnight => start.AddDays(14 * n),
        _ => start.AddMonths(n),
    };
}

/// <summary>A lease: the tenants, rent and bond. The rent is due in advance from Start until the tenants leave.</summary>
public sealed record Tenancy(int Id, int PropertyId, string Tenants, string? Email, string? Phone, DateOnly Start, DateOnly? LeaseEnd, decimal Rent,
    string Frequency, decimal? Bond, string? BondLodgement, DateOnly? RentReview, DateOnly? Vacated, string? Note)
{
    public bool Current(DateOnly today) => Start <= today && (Vacated is null || Vacated >= today);
}

public sealed record RentPayment(int Id, int TenancyId, DateOnly Paid, decimal Amount, string? Note);

/// <summary>The rent owing on a tenancy at a date: what was due, what was paid, and how far ahead or behind it is.</summary>
public sealed record RentPosition(Tenancy Tenancy, decimal Due, decimal Paid, DateOnly? PaidTo, DateOnly? NextDue, DateOnly? OldestUnpaid)
{
    public decimal Arrears => Math.Max(0, Due - Paid);
    public decimal Credit => Math.Max(0, Paid - Due);
    public int DaysBehind(DateOnly today) => OldestUnpaid is { } d && Arrears > 0 ? today.DayNumber - d.DayNumber : 0;
}

public static class RentLedger
{
    /// <summary>Rent is treated as missed this many days after its due date, before an alert goes out.</summary>
    public const int GraceDays = 3;

    /// <summary>Each due date and amount from the start up to the date (and before the tenants left).</summary>
    public static IReadOnlyList<(DateOnly Date, decimal Amount)> Schedule(Tenancy t, DateOnly until)
    {
        var due = new List<(DateOnly, decimal)>();
        var last = t.Vacated is { } v && v < until ? v : until;
        for (var n = 0; n < 5000; n++)
        {
            var d = RentFrequencies.Next(t.Frequency, t.Start, n);
            if (d > last)
                break;
            due.Add((d, t.Rent));
        }
        return due;
    }

    /// <summary>
    /// The tenancy's position at the date. Payments go against the oldest rent first, so PaidTo is the day before the first
    /// period not fully paid and OldestUnpaid is that period's due date.
    /// </summary>
    public static RentPosition Position(Tenancy t, IEnumerable<RentPayment> payments, DateOnly asAt)
    {
        var paid = payments.Where(p => p.TenancyId == t.Id && p.Paid <= asAt).Sum(p => p.Amount);
        var schedule = Schedule(t, asAt);
        var due = schedule.Sum(s => s.Amount);
        // Periods covered by what was paid, including ones paid in advance
        var left = paid;
        DateOnly? paidTo = null;
        for (var n = 0; n < 5000 && left >= t.Rent && t.Rent > 0; n++)
        {
            left -= t.Rent;
            paidTo = RentFrequencies.Next(t.Frequency, t.Start, n + 1).AddDays(-1);
        }
        var unpaid = schedule.Select(s => s.Date).FirstOrDefault(d => paidTo is null || d > paidTo.Value);
        DateOnly? next = t.Vacated is { } v && v < asAt ? null : schedule.Count == 0 ? t.Start : RentFrequencies.Next(t.Frequency, t.Start, schedule.Count);
        return new RentPosition(t, due, paid, paidTo, next, unpaid == default ? null : unpaid);
    }
}

public static class TenantBillCategories
{
    /// <summary>The expenses a bill to the tenant usually recovers.</summary>
    public static readonly IReadOnlyList<string> Keys = ["Water", "Cleaning", "Repairs", "Gardening", "PestControl", "Sundry"];
}

/// <summary>A cost passed on to the tenant. Once paid it nets off the expense on the schedule (in the year it was paid).</summary>
public sealed record TenantBill(int Id, int PropertyId, int? TenancyId, string Category, string Description, DateOnly Billed, decimal Amount, DateOnly? Paid)
{
    public string CategoryName => RentalCategories.Find(Category)?.Name ?? Category;
}

public static class JobTreatments
{
    public const string Repair = "Repair", Initial = "Initial", Improvement = "Improvement";
    public static readonly IReadOnlyList<string> All = [Repair, Initial, Improvement];

    public static string Text(string t) => t switch
    {
        Repair => "Repair or maintenance (deductible)",
        Initial => "Initial repair (capital)",
        _ => "Improvement (capital)",
    };
}

/// <summary>
/// A repair job. A repair goes on the schedule as repairs and maintenance in the year it was done; an initial repair or an
/// improvement is capital (capital works, or the cost base when the property is sold).
/// </summary>
public sealed record RentalJob(int Id, int PropertyId, DateOnly Done, string Description, string? Tradesperson, decimal Cost, string Treatment, string? InvoiceRef)
{
    public bool IsCapital => Treatment != JobTreatments.Repair;
}

/// <summary>Part of a capital job split out as a depreciating asset (Division 40): its share of the cost and how it is written off.</summary>
public sealed record JobPart(string Description, decimal Amount, string Method, decimal? LifeYears);

/// <summary>
/// Splits an initial repair or improvement between depreciating assets (an oven, a hot water system, carpet) and capital works
/// (the rest of the job: the building work itself), so each part is written off the right way.
/// </summary>
public static class JobSplit
{
    /// <summary>A low-cost asset (under $1,000) can go in the low-value pool.</summary>
    public const decimal LowCostLimit = 1_000m;

    public const decimal CapitalWorksRate = 2.5m;

    public static (IReadOnlyList<TaxWork.RentalAsset> Assets, string? Error) Build(RentalJob job, IEnumerable<JobPart> parts)
    {
        var list = parts.Where(p => p.Amount > 0 || !string.IsNullOrWhiteSpace(p.Description)).ToList();
        foreach (var p in list)
        {
            if (string.IsNullOrWhiteSpace(p.Description) || p.Amount <= 0)
                return ([], "Give each depreciating asset a description and its part of the cost.");
            if (p.Method == TaxWork.RentalAssetKinds.LowValuePool && p.Amount >= LowCostLimit)
                return ([], $"{p.Description.Trim()} costs {LowCostLimit:C0} or more, so it can't go in the low-value pool.");
            if (p.Method != TaxWork.RentalAssetKinds.LowValuePool && p.LifeYears is not > 0)
                return ([], $"Enter the effective life of {p.Description.Trim()} (the ATO's ruling TR 2022/1 lists them).");
        }
        var parted = list.Sum(p => p.Amount);
        if (parted > job.Cost)
            return ([], $"The assets add up to {parted:C}, more than the job's cost of {job.Cost:C}.");
        var assets = list.Select(p => new TaxWork.RentalAsset(0, job.PropertyId, TaxWork.RentalAssetKinds.Plant, p.Description.Trim(), job.Done, p.Amount,
                p.Method == TaxWork.RentalAssetKinds.LowValuePool ? null : p.LifeYears,
                p.Method is TaxWork.RentalAssetKinds.PrimeCost or TaxWork.RentalAssetKinds.LowValuePool ? p.Method : TaxWork.RentalAssetKinds.DiminishingValue,
                null, null))
            .ToList();
        if (job.Cost - parted > 0)
            assets.Add(new TaxWork.RentalAsset(0, job.PropertyId, TaxWork.RentalAssetKinds.Building, job.Description, job.Done, job.Cost - parted,
                null, null, CapitalWorksRate, null));
        return (assets, null);
    }
}

public static class InspectionKinds
{
    public const string Entry = "Entry", Routine = "Routine", Exit = "Exit";
    public static readonly IReadOnlyList<string> All = [Entry, Routine, Exit];

    public static string Text(string k) => k switch { Entry => "Entry condition report", Exit => "Exit condition report", _ => "Routine inspection" };
}

public sealed record RentalInspection(int Id, int PropertyId, int? TenancyId, string Kind, DateOnly Done, string? Notes);

/// <summary>A check or renewal that comes round again: due Months after it was last done, or on Due when that is set.</summary>
public sealed record ComplianceItem(int Id, int PropertyId, string Kind, DateOnly? LastDone, int? Months, DateOnly? Due, string? Note)
{
    public DateOnly? NextDue => Due ?? (LastDone is { } d && Months is > 0 ? d.AddMonths(Months.Value) : null);
}

/// <summary>A check most landlords in the state need, with how often (months). Check them against the state's current rules.</summary>
public sealed record CompliancePreset(string Kind, int Months);

public static class CompliancePresets
{
    public static readonly IReadOnlyList<string> States = ["NSW", "VIC", "QLD", "WA", "SA", "TAS", "ACT", "NT"];

    private static readonly CompliancePreset[] Everywhere =
    [
        new("Smoke alarm check", 12),
        new("Landlord insurance renewal", 12),
        new("Building insurance renewal", 12),
    ];

    private static readonly Dictionary<string, CompliancePreset[]> ByState = new()
    {
        ["NSW"] = [new("Pool compliance certificate (if there is a pool)", 36)],
        ["VIC"] = [new("Gas safety check", 24), new("Electrical safety check", 24), new("Pool barrier compliance certificate (if there is a pool)", 48)],
        ["QLD"] = [new("Pool safety certificate (if there is a pool)", 12)],
        ["WA"] = [new("Residual current devices (safety switches) check", 12), new("Pool barrier inspection (if there is a pool)", 48)],
        ["SA"] = [],
        ["TAS"] = [],
        ["ACT"] = [new("Minimum energy efficiency (ceiling insulation) check", 60)],
        ["NT"] = [],
    };

    /// <summary>The usual checks for a property in the state (or everywhere's when the state isn't set).</summary>
    public static IReadOnlyList<CompliancePreset> For(string? state) =>
        [.. Everywhere, .. (state is not null && ByState.TryGetValue(state, out var extra) ? extra : [])];
}

/// <summary>Something coming up on a property: a lease ending, a rent review, a check or renewal, a bond not lodged, rent missed.</summary>
public sealed record PropertyReminder(int PropertyId, string Address, DateOnly? Due, string Text, string Key, bool Urgent);

public static class PropertyReminders
{
    public const int LeaseDays = 60, ReviewDays = 30, ComplianceDays = 30;

    /// <summary>What's coming up in the next weeks, or overdue, for the properties.</summary>
    public static IReadOnlyList<PropertyReminder> Due(IEnumerable<RentalProperty> properties, IEnumerable<Tenancy> tenancies, IEnumerable<RentPayment> payments,
        IEnumerable<ComplianceItem> compliance, DateOnly today)
    {
        var byId = properties.ToDictionary(p => p.Id);
        var paymentList = payments.ToList();
        var list = new List<PropertyReminder>();
        foreach (var t in tenancies.Where(t => byId.ContainsKey(t.PropertyId) && t.Current(today)))
        {
            var address = byId[t.PropertyId].Address;
            if (t.LeaseEnd is { } end && end >= today.AddDays(-30) && end <= today.AddDays(LeaseDays))
                list.Add(new(t.PropertyId, address, end, $"{t.Tenants}'s lease {(end < today ? "ended" : "ends")} on {end:d MMM yyyy}.", $"lease:{t.Id}:{end:yyyyMMdd}", end <= today));
            if (t.RentReview is { } review && review >= today.AddDays(-30) && review <= today.AddDays(ReviewDays))
                list.Add(new(t.PropertyId, address, review, $"Rent review for {t.Tenants} on {review:d MMM yyyy}.", $"review:{t.Id}:{review:yyyyMMdd}", review <= today));
            if (t.Bond is > 0 && string.IsNullOrWhiteSpace(t.BondLodgement))
                list.Add(new(t.PropertyId, address, null, $"{t.Tenants}'s bond of {t.Bond:C} has no lodgment number recorded.", $"bond:{t.Id}", false));
            var position = RentLedger.Position(t, paymentList, today);
            if (position.Arrears > 0 && position.DaysBehind(today) > RentLedger.GraceDays)
                list.Add(new(t.PropertyId, address, position.OldestUnpaid,
                    $"{t.Tenants} is {position.Arrears:C} behind with the rent (due {position.OldestUnpaid:d MMM yyyy}).",
                    $"arrears:{t.Id}:{position.OldestUnpaid:yyyyMMdd}", true));
        }
        foreach (var c in compliance.Where(c => byId.ContainsKey(c.PropertyId)))
            if (c.NextDue is { } due && due <= today.AddDays(ComplianceDays))
                list.Add(new(c.PropertyId, byId[c.PropertyId].Address, due, $"{c.Kind} {(due < today ? "was due" : "is due")} on {due:d MMM yyyy}.",
                    $"check:{c.Id}:{due:yyyyMMdd}", due < today));
        return list.OrderBy(r => r.Due ?? DateOnly.MaxValue).ThenBy(r => r.Address).ToList();
    }
}
