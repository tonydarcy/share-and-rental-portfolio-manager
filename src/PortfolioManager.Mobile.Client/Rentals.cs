namespace PortfolioManager.Mobile.Client;

/// <summary>A rental property (the list_rental_properties tool). Entries need its payout code to reach its schedule.</summary>
public sealed record RentalPropertySummary(
    int PropertyId, string Address, string? PayoutCode, decimal OwnershipPct, DateOnly? FirstEarned, bool Active, string? Note,
    decimal? Value = null, DateOnly? Valued = null);

/// <summary>One line of a property's schedule: the whole property's amount and the owner's share.</summary>
public sealed record RentalScheduleLine(string Name, decimal WholeProperty, decimal OwnersShare);

public sealed record RentalPropertyYear(
    int Id, string Address, decimal OwnershipPct, decimal WeeksRented, decimal ExpensesClaimedPct, IReadOnlyList<RentalScheduleLine> Lines,
    decimal GrossRent21P, decimal Interest21Q, decimal CapitalWorks21F, decimal OtherDeductions21U, decimal NetRent);

public sealed record RentalItem21(decimal GrossRent21P, decimal Interest21Q, decimal CapitalWorks21F, decimal OtherDeductions21U, decimal NetRent);

/// <summary>Every property's schedule for a financial year (the report_rental_schedule tool).</summary>
public sealed record RentalSchedule(string? FinancialYear, RentalItem21 Item21, IReadOnlyList<RentalPropertyYear> Properties);

/// <summary>Rent and expenses for add_rental_entry: the whole property's amounts by name, positive.</summary>
public sealed record RentalRequest(DateOnly Date, string PropertyCode, IReadOnlyDictionary<string, decimal> Amounts, string? Comment = null);

/// <summary>An amount add_rental_entry takes: its name, the words to show and whether it's income.</summary>
public sealed record RentalAmount(string Key, string Label, bool IsIncome);

public static class RentalAmounts
{
    /// <summary>The amounts the app offers, in the site's order (the sundry expenses are left to the site).</summary>
    public static readonly IReadOnlyList<RentalAmount> All =
    [
        new("rentalIncome", "Rent received", true),
        new("otherRentalRelatedIncome", "Other rental income", true),
        new("advertisingForTenants", "Advertising for tenants", false),
        new("bodyCorporateFees", "Body corporate fees", false),
        new("borrowingExpenses", "Borrowing expenses", false),
        new("cleaning", "Cleaning", false),
        new("councilRates", "Council rates", false),
        new("gardeningAndLawnMowing", "Gardening and lawn mowing", false),
        new("insurance", "Insurance", false),
        new("interestOnLoans", "Interest on loans", false),
        new("landTax", "Land tax", false),
        new("legalFees", "Legal fees", false),
        new("pestControl", "Pest control", false),
        new("propertyAgentFeesAndCommission", "Agent fees and commission", false),
        new("repairsAndMaintenance", "Repairs and maintenance", false),
        new("stationaryTelephoneAndPostage", "Stationery, phone and postage", false),
        new("travel", "Travel", false),
        new("waterCharges", "Water charges", false),
    ];

    public static IEnumerable<RentalAmount> Expenses => All.Where(a => !a.IsIncome);

    public static decimal Net(IReadOnlyDictionary<string, decimal> amounts) =>
        amounts.Sum(a => All.FirstOrDefault(f => f.Key == a.Key)?.IsIncome == true ? a.Value : -a.Value);
}
