using PortfolioManager.Core.Invoicing;
using PortfolioManager.Core.SalesTax;

namespace PortfolioManager.Core.Bills;

public static class BillKinds
{
    public const string Bill = "Bill", SupplierCredit = "SupplierCredit";
}

public sealed record Supplier(
    int SupplierId,
    string Name,
    string? ContactName,
    string? Email,
    string? Phone,
    string? Address,
    string? TaxNumber,
    string? PaymentDetails,
    int? DefaultAccountId,
    int? DefaultTaxCodeId,
    int? DueDays,
    bool IsActive,
    int BillCount = 0,
    decimal Owing = 0);

/// <summary>A bill or supplier credit in the list, with what has been paid.</summary>
public sealed record BillSummary(
    int BillId,
    string Kind,
    string? Number,
    int SupplierId,
    string SupplierName,
    DateOnly IssueDate,
    DateOnly DueDate,
    string? Reference,
    string Status,
    decimal Total,
    decimal Paid,
    string? Currency,
    decimal? HomeTotal)
{
    public bool IsCredit => Kind == BillKinds.SupplierCredit;
    public decimal Balance => Status == InvoiceStatus.Approved ? Total - Paid : 0;

    /// <summary>What is owing in the portfolio's currency, at the bill's rate.</summary>
    public decimal HomeBalance => Currency is null || HomeTotal is not { } home || Total == 0 ? Balance : decimal.Round(Balance * home / Total, 2);

    public string DisplayStatus(DateOnly today) => InvoiceRules.DisplayStatus(Status, Total, Paid, DueDate, today);
}

public sealed record BillPayment(int PaymentId, DateOnly Date, decimal Amount, int AccountId, string AccountName, string? Reference, int? JournalId,
    int? CreditId, decimal? ExchangeRate, decimal? HomeAmount);

public sealed record BillDetail(
    int BillId,
    string Kind,
    string? Number,
    int SupplierId,
    string SupplierName,
    DateOnly IssueDate,
    DateOnly DueDate,
    string? Reference,
    string? Notes,
    bool AmountsIncludeTax,
    string Status,
    decimal Net,
    decimal Tax,
    decimal Total,
    string? Currency,
    decimal? ExchangeRate,
    decimal? HomeTotal,
    int? PurchaseOrderId,
    int? JournalId,
    IReadOnlyList<InvoiceLine> Lines,
    IReadOnlyList<BillPayment> Payments)
{
    public bool IsCredit => Kind == BillKinds.SupplierCredit;
    public decimal Paid => Payments.Sum(p => p.Amount);
    public decimal Balance => Status == InvoiceStatus.Approved ? Total - Paid : 0;
    public bool IsDraft => Status == InvoiceStatus.Draft;
    public bool IsVoid => Status == InvoiceStatus.Void;
    public string Title => (IsCredit ? "Supplier credit " : "Bill ") + (Number ?? $"#{BillId}");
    public string DisplayStatus(DateOnly today) => InvoiceRules.DisplayStatus(Status, Total, Paid, DueDate, today);
}

/// <summary>The bill form. Lines work as on invoices; a tracked item's line goes to its inventory account.</summary>
public sealed class BillInput : DocumentInput
{
    public int SupplierId { get; set; }
    public string Kind { get; set; } = BillKinds.Bill;
    public string? Number { get; set; }
    public DateTime IssueDate { get; set; }
    public DateTime DueDate { get; set; }
    public string? Reference { get; set; }
    public string? Notes { get; set; }
    public int? PurchaseOrderId { get; set; }

    public static BillInput New(DateTime today, int dueDays, int defaultAccountId) => new()
    {
        IssueDate = today.Date,
        DueDate = today.Date.AddDays(dueDays),
        Lines = [new InvoiceLineInput { Quantity = 1, AccountId = defaultAccountId }],
    };

    public static BillInput From(BillDetail b) => new()
    {
        SupplierId = b.SupplierId,
        Kind = b.Kind,
        Number = b.Number,
        IssueDate = b.IssueDate.ToDateTime(TimeOnly.MinValue),
        DueDate = b.DueDate.ToDateTime(TimeOnly.MinValue),
        Reference = b.Reference,
        Notes = b.Notes,
        AmountsIncludeTax = b.AmountsIncludeTax,
        Currency = b.Currency,
        ExchangeRate = b.ExchangeRate,
        PurchaseOrderId = b.PurchaseOrderId,
        Lines = [.. b.Lines.Select(l => new InvoiceLineInput
        {
            Description = l.Description, Quantity = l.Quantity, UnitPrice = l.UnitPrice, AccountId = l.AccountId, TaxCodeId = l.TaxCodeId,
            ItemId = l.ItemId, Tracking1Id = l.Tracking1Id, Tracking2Id = l.Tracking2Id,
        })],
    };

    public IReadOnlyList<string> Validate(IReadOnlySet<int> suppliers, IReadOnlySet<int> accounts, IReadOnlyDictionary<int, TaxCode> taxCodes)
    {
        var errors = new List<string>();
        if (!suppliers.Contains(SupplierId))
            errors.Add("Choose a supplier.");
        if (Kind is not (BillKinds.Bill or BillKinds.SupplierCredit))
            errors.Add("Choose a bill or a supplier credit.");
        if (IssueDate == default || DueDate == default)
            errors.Add("Enter the bill date and the due date.");
        else if (DueDate.Date < IssueDate.Date)
            errors.Add("The due date can't be before the bill date.");
        if ((Number?.Trim().Length ?? 0) > 60)
            errors.Add("The supplier's invoice number can be at most 60 characters.");
        ValidateLines(errors, accounts, taxCodes, "choose the account it goes to");
        if (errors.Count == 0 && Totals(taxCodes).Total <= 0)
            errors.Add("The total must be more than zero.");
        return errors;
    }
}

/// <summary>The add / edit supplier form.</summary>
public sealed class SupplierInput
{
    public int? SupplierId { get; set; }
    public string? Name { get; set; }
    public string? ContactName { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? Address { get; set; }
    public string? TaxNumber { get; set; }
    public string? PaymentDetails { get; set; }
    public int? DefaultAccountId { get; set; }
    public int? DefaultTaxCodeId { get; set; }
    public int? DueDays { get; set; }
    public bool IsActive { get; set; } = true;

    public static SupplierInput From(Supplier s) => new()
    {
        SupplierId = s.SupplierId, Name = s.Name, ContactName = s.ContactName, Email = s.Email, Phone = s.Phone, Address = s.Address,
        TaxNumber = s.TaxNumber, PaymentDetails = s.PaymentDetails, DefaultAccountId = s.DefaultAccountId, DefaultTaxCodeId = s.DefaultTaxCodeId,
        DueDays = s.DueDays, IsActive = s.IsActive,
    };

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Name))
            errors.Add("Enter the supplier's name.");
        else if (Name.Trim().Length > 200)
            errors.Add("The name can be at most 200 characters.");
        if (!string.IsNullOrWhiteSpace(Email) && (Email.Trim().Length > 500 || !Email.Contains('@')))
            errors.Add("Check the email address.");
        if ((Address?.Length ?? 0) > 500 || (PaymentDetails?.Length ?? 0) > 500)
            errors.Add("The address and payment details can be at most 500 characters each.");
        if ((Phone?.Trim().Length ?? 0) > 50 || (TaxNumber?.Trim().Length ?? 0) > 50)
            errors.Add("The phone number and tax number can be at most 50 characters.");
        if (DueDays is < 0 or > 365)
            errors.Add("Days to pay must be between 0 and 365.");
        return errors;
    }
}

/// <summary>One supplier's balance by how overdue it is, in the portfolio's currency.</summary>
public sealed record AgedPayable(int SupplierId, string Supplier, decimal Current, decimal Days30, decimal Days60, decimal Days90, decimal Older)
{
    public decimal Total => Current + Days30 + Days60 + Days90 + Older;
}

public static class Payables
{
    /// <summary>
    /// What is owing to each supplier as at a date, by days past due: not yet due, 1-30, 31-60, 61-90 and over 90.
    /// A supplier credit not yet applied reduces what is owing.
    /// </summary>
    public static IReadOnlyList<AgedPayable> Aged(IEnumerable<BillSummary> bills, DateOnly asAt) =>
        bills.Where(b => b.Status == InvoiceStatus.Approved && b.IssueDate <= asAt && b.Balance != 0)
            .GroupBy(b => (b.SupplierId, b.SupplierName))
            .Select(g =>
            {
                decimal current = 0, d30 = 0, d60 = 0, d90 = 0, older = 0;
                foreach (var b in g)
                {
                    var owing = b.IsCredit ? -b.HomeBalance : b.HomeBalance;
                    var days = asAt.DayNumber - b.DueDate.DayNumber;
                    switch (days)
                    {
                        case <= 0: current += owing; break;
                        case <= 30: d30 += owing; break;
                        case <= 60: d60 += owing; break;
                        case <= 90: d90 += owing; break;
                        default: older += owing; break;
                    }
                }
                return new AgedPayable(g.Key.SupplierId, g.Key.SupplierName, current, d30, d60, d90, older);
            })
            .Where(a => a.Total != 0)
            .OrderBy(a => a.Supplier)
            .ToList();
}

public sealed record RepeatingBill(int Id, int SupplierId, string SupplierName, string Name, string Frequency, DateOnly Start, DateOnly? End,
    int PostedCount, int DueDays, bool Approve, bool AmountsIncludeTax, string? Reference, string? Notes, IReadOnlyList<InvoiceLineInput> Lines,
    bool Active, DateOnly? LastCreated)
{
    public DateOnly NextDate => Accounting.RepeatFrequencies.Occurrence(Start, Frequency, PostedCount);

    public IReadOnlyList<DateOnly> DueDates(DateOnly asAt, int max = 36)
    {
        var dates = new List<DateOnly>();
        for (var n = PostedCount; Active && dates.Count < max; n++)
        {
            var d = Accounting.RepeatFrequencies.Occurrence(Start, Frequency, n);
            if (d > asAt || (End is { } end && d > end))
                break;
            dates.Add(d);
        }
        return dates;
    }

    public BillInput ToInput(DateOnly date) => new()
    {
        SupplierId = SupplierId,
        IssueDate = date.ToDateTime(TimeOnly.MinValue),
        DueDate = date.AddDays(DueDays).ToDateTime(TimeOnly.MinValue),
        Reference = Reference,
        Notes = Notes,
        AmountsIncludeTax = AmountsIncludeTax,
        Lines = Lines.Select(l => l.Copy()).ToList(),
    };
}
