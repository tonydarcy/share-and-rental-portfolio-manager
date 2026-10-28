namespace PortfolioManager.Core.Payouts;

/// <summary>One rental amount: its tblPayouts column (also the proc parameter, with @) and its label.</summary>
public sealed record RentalField(string Column, string Label, bool IsIncome);

/// <summary>A rental property payout as entered on the Add/Edit rental payout pages.</summary>
public sealed class RentalPayoutInput
{
    /// <summary>Every rental amount uspAddRentalPayout takes, in the proc's order.</summary>
    public static readonly IReadOnlyList<RentalField> Fields =
    [
        new("dblRentalIncomeAmount", "Rent received", true),
        new("dblOtherRentalRelatedIncomeAmount", "Other rental income", true),
        new("dblAdvertisingForTenantsExpenseAmount", "Advertising for tenants", false),
        new("dblBodyCorportateFeesExpenseAmount", "Body corporate fees", false),
        new("dblBorrowingExpensesAmount", "Borrowing expenses", false),
        new("dblCleaningExpenseAmount", "Cleaning", false),
        new("dblCouncilRatesExpenseAmount", "Council rates", false),
        new("dblCapitalAllowancesExpenseAmount", "Capital allowances (depreciation)", false),
        new("dblGardeningAndLawnMowingExpenseAmount", "Gardening and lawn mowing", false),
        new("dblInsuranceExpenseAmount", "Insurance", false),
        new("dblInterestOnLoansExpenseAmount", "Interest on loans", false),
        new("dblLandTaxExpenseAmount", "Land tax", false),
        new("dblLegalFeesExpenseAmount", "Legal fees", false),
        new("dblPestControlExpenseAmount", "Pest control", false),
        new("dblPropertyAgentFeesAndCommissionExpenseAmount", "Property agent fees and commission", false),
        new("dblRepairsAndMaintenanceExpenseAmount", "Repairs and maintenance", false),
        new("dblCapitalWorksSpecialBuildWriteOffExpenseAmount", "Capital works deductions", false),
        new("dblStationaryTelephoneAndPostageExpenseAmount", "Stationery, telephone and postage", false),
        new("dblTravelExpenseAmount", "Travel", false),
        new("dblWaterChargesExpenseAmount", "Water charges", false),
        new("dblSundryRentalExpense1Amount", "Sundry expense 1", false),
        new("dblSundryRentalExpense2Amount", "Sundry expense 2", false),
        new("dblSundryRentalExpense3Amount", "Sundry expense 3", false),
        new("dblSundryRentalExpense4Amount", "Sundry expense 4", false),
        new("dblSundryRentalExpense5Amount", "Sundry expense 5", false),
        new("dblSundryRentalExpense6Amount", "Sundry expense 6", false),
        new("dblSundryRentalExpense7Amount", "Sundry expense 7", false),
        new("dblSundryRentalExpense8Amount", "Sundry expense 8", false),
        new("dblSundryRentalExpense9Amount", "Sundry expense 9", false),
        new("dblSundryRentalExpense10Amount", "Sundry expense 10", false),
    ];

    public string PropertyCode { get; set; } = "";
    public string ExchangeCode { get; set; } = "PROP";
    public DateTime Date { get; set; } = DateTime.Today;
    public string? Comment { get; set; }

    /// <summary>One amount per entry in <see cref="Fields"/>, same order.</summary>
    public decimal[] Amounts { get; set; } = new decimal[Fields.Count];

    /// <summary>The amount for field i (zero if the form didn't send it).</summary>
    public decimal Amount(int i) => i >= 0 && i < Amounts.Length ? Amounts[i] : 0m;

    public decimal Income => Fields.Select((f, i) => f.IsIncome ? Amount(i) : 0m).Sum();
    public decimal Expenses => Fields.Select((f, i) => f.IsIncome ? 0m : Amount(i)).Sum();

    /// <summary>What uspAddRentalPayout saves as the total: income less expenses (can be negative).</summary>
    public decimal Net => Income - Expenses;

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(PropertyCode))
            errors.Add("Enter a short name or code for the property.");
        else if (PropertyCode.Trim().Length > 50)
            errors.Add("The property code can be at most 50 characters.");
        if (string.IsNullOrWhiteSpace(ExchangeCode))
            errors.Add("Enter the exchange code for the property (e.g. PROP).");
        else if (ExchangeCode.Trim().Length > 50)
            errors.Add("The exchange code can be at most 50 characters.");
        if (Date.Year < 1900 || Date.Year > 2078)
            errors.Add("Enter a valid date.");
        if ((Comment?.Length ?? 0) > 500)
            errors.Add("The comment can be at most 500 characters.");
        if (Amounts.Length > Fields.Count)
            errors.Add("Too many amounts were sent.");
        else if (Amounts.Any(a => a < 0))
            errors.Add("Amounts can't be negative. Enter expenses as positive amounts.");
        else if (Amounts.All(a => a == 0))
            errors.Add("Enter the rent received or at least one expense.");
        return errors;
    }

    /// <summary>
    /// The short name the API uses for a field: the column without "dbl" and "Amount"/"ExpenseAmount", in camelCase
    /// (rentalIncome, councilRates, interestOnLoans, sundryExpense1...).
    /// </summary>
    public static string Key(RentalField f)
    {
        var name = f.Column["dbl".Length..].Replace("Corportate", "Corporate");
        foreach (var end in new[] { "ExpenseAmount", "Amount" })
            if (name.EndsWith(end, StringComparison.Ordinal))
            {
                name = name[..^end.Length];
                break;
            }
        name = name.Replace("SundryRentalExpense", "SundryExpense");
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    /// <summary>Sets the amounts named by <see cref="Key"/> (any case), leaving the rest as they are. Returns the names it didn't know.</summary>
    public IReadOnlyList<string> SetAmounts(IReadOnlyDictionary<string, decimal> amounts)
    {
        if (Amounts.Length < Fields.Count)
            Amounts = [.. Amounts, .. new decimal[Fields.Count - Amounts.Length]];
        var unknown = new List<string>();
        foreach (var (name, amount) in amounts)
        {
            var i = Fields.Select(Key).ToList().FindIndex(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
            if (i < 0)
                unknown.Add(name);
            else
                Amounts[i] = amount;
        }
        return unknown;
    }

    /// <summary>The amounts by <see cref="Key"/>, leaving out zeros.</summary>
    public IReadOnlyDictionary<string, decimal> NamedAmounts() =>
        Fields.Select((f, i) => (Key(f), Amount(i))).Where(a => a.Item2 != 0).ToDictionary(a => a.Item1, a => a.Item2);

    /// <summary>Parameters for uspAddRentalPayout (every one is required).</summary>
    public Dictionary<string, object?> AddParameters(int portfolioId)
    {
        var p = new Dictionary<string, object?>
        {
            ["iUserId"] = portfolioId,
            ["strStockExchangeCode"] = ExchangeCode.Trim().ToUpperInvariant(),
            ["strStockCode"] = PropertyCode.Trim().ToUpperInvariant(),
            ["dtmTransactionDate"] = Date.Date,
            ["strComment"] = string.IsNullOrWhiteSpace(Comment) ? null : Comment.Trim(),
        };
        for (var i = 0; i < Fields.Count; i++)
            p[Fields[i].Column] = (double)Amount(i);
        return p;
    }

    /// <summary>Parameters for uspUpdateRentalPayout.</summary>
    public Dictionary<string, object?> UpdateParameters(int portfolioId, int payoutId)
    {
        var p = AddParameters(portfolioId);
        p["iPayoutID"] = payoutId;
        return p;
    }

    /// <summary>A tblPayouts row (the columns GetRentalAsync selects) as form input.</summary>
    public static RentalPayoutInput FromRow(IDictionary<string, object?> row)
    {
        var r = new RentalPayoutInput
        {
            PropertyCode = Text(row, "strStockCode"),
            ExchangeCode = Text(row, "strStockExchangeCode"),
            Date = row.TryGetValue("dtmTransactionDate", out var d) && d is DateTime date ? date.Date : DateTime.Today,
            Comment = Text(row, "strComment") is { Length: > 0 } c ? c : null,
        };
        for (var i = 0; i < Fields.Count; i++)
            r.Amounts[i] = row.TryGetValue(Fields[i].Column, out var v) && v is not null and not DBNull
                ? Math.Round(Convert.ToDecimal(v), 2)
                : 0m;
        return r;
    }

    private static string Text(IDictionary<string, object?> row, string column) =>
        row.TryGetValue(column, out var v) && v is not null and not DBNull ? Convert.ToString(v)!.Trim() : "";
}
