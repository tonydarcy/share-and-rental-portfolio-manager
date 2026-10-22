using System.Globalization;
using System.Xml.Linq;
using PortfolioManager.Core.SalesTax;

namespace PortfolioManager.Core.Invoicing;

public static class InvoiceStatus
{
    public const string Draft = "Draft";
    public const string Approved = "Approved";
    public const string Void = "Void";
}

public sealed record Customer(
    int CustomerId,
    string Name,
    string? ContactName,
    string? Email,
    string? Phone,
    string? Address,
    string? TaxNumber,
    bool IsActive,
    int InvoiceCount = 0,
    decimal Outstanding = 0);

/// <summary>An invoice in the list, with what has been paid.</summary>
public sealed record InvoiceSummary(
    int InvoiceId,
    string Number,
    int CustomerId,
    string CustomerName,
    DateOnly IssueDate,
    DateOnly DueDate,
    string? Reference,
    string Status,
    decimal Net,
    decimal Tax,
    decimal Total,
    decimal Paid,
    string? Currency = null,
    decimal? HomeTotal = null)
{
    public decimal Balance => Status == InvoiceStatus.Approved ? Total - Paid : 0;

    /// <summary>What is owing in the portfolio's currency (at the invoice's rate) when the invoice is in another currency.</summary>
    public decimal HomeBalance => Currency is null || HomeTotal is not { } home || Total == 0 ? Balance : decimal.Round(Balance * home / Total, 2);

    /// <summary>What to call it in a list: Draft, Void, Paid, Part paid, Overdue or Unpaid.</summary>
    public string DisplayStatus(DateOnly today) => InvoiceRules.DisplayStatus(Status, Total, Paid, DueDate, today);
}

public sealed record InvoiceLine(
    int LineId,
    string Description,
    decimal Quantity,
    decimal UnitPrice,
    int AccountId,
    int? TaxCodeId,
    string? TaxCode,
    decimal Net,
    decimal Tax,
    decimal Total,
    int? ItemId = null,
    int? Tracking1Id = null,
    int? Tracking2Id = null);

public sealed record InvoicePayment(int PaymentId, DateOnly Date, decimal Amount, int AccountId, string AccountName, string? Reference, int? JournalId,
    decimal? ExchangeRate = null, decimal? HomeAmount = null);

public sealed record InvoiceDetail(
    int InvoiceId,
    string Number,
    int CustomerId,
    DateOnly IssueDate,
    DateOnly DueDate,
    string? Reference,
    string? Notes,
    bool AmountsIncludeTax,
    string Status,
    decimal Net,
    decimal Tax,
    decimal Total,
    int? JournalId,
    Customer Customer,
    IReadOnlyList<InvoiceLine> Lines,
    IReadOnlyList<InvoicePayment> Payments,
    string? Currency = null,
    decimal? ExchangeRate = null,
    decimal? HomeTotal = null)
{
    public decimal Paid => Payments.Sum(p => p.Amount);
    public decimal Balance => Status == InvoiceStatus.Approved ? Total - Paid : 0;
    public bool IsDraft => Status == InvoiceStatus.Draft;
    public bool IsVoid => Status == InvoiceStatus.Void;
    public string DisplayStatus(DateOnly today) => InvoiceRules.DisplayStatus(Status, Total, Paid, DueDate, today);
}

public sealed record InvoiceSettings(string NumberPrefix, int NextNumber, int DueDays, bool AmountsIncludeTax, string? PaymentDetails, string? Footer)
{
    public static readonly InvoiceSettings Default = new("INV-", 1, 14, false, null, null);
}

public static class InvoiceRules
{
    public static string DisplayStatus(string status, decimal total, decimal paid, DateOnly due, DateOnly today) => status switch
    {
        InvoiceStatus.Draft => "Draft",
        InvoiceStatus.Void => "Void",
        _ when paid >= total => "Paid",
        _ when due < today => "Overdue",
        _ when paid > 0 => "Part paid",
        _ => "Unpaid",
    };

    /// <summary>
    /// A line's amount before tax, its tax and its total. The quantity times the price is the amount
    /// including tax when the invoice's amounts include tax, otherwise before it. Rounded to the cent.
    /// </summary>
    public static (decimal Net, decimal Tax, decimal Total) Amounts(decimal quantity, decimal unitPrice, TaxCode? code, bool amountsIncludeTax)
    {
        var amount = decimal.Round(quantity * unitPrice, 2, MidpointRounding.AwayFromZero);
        if (code is null)
            return (amount, 0, amount);
        if (amountsIncludeTax)
        {
            var tax = TaxMath.TaxInGross(amount, code.Rate);
            return (amount - tax, tax, amount);
        }
        var added = TaxMath.TaxOnNet(amount, code.Rate);
        return (amount, added, amount + added);
    }
}

/// <summary>One line of the invoice form.</summary>
public sealed class InvoiceLineInput
{
    public string? Description { get; set; }
    public decimal? Quantity { get; set; }
    public decimal? UnitPrice { get; set; }
    public int AccountId { get; set; }
    public int? TaxCodeId { get; set; }

    /// <summary>An inventory item (a tracked one comes out of or goes into stock when the invoice or bill is approved).</summary>
    public int? ItemId { get; set; }
    public int? Tracking1Id { get; set; }
    public int? Tracking2Id { get; set; }

    /// <summary>A row left empty is ignored.</summary>
    public bool IsBlank => string.IsNullOrWhiteSpace(Description) && UnitPrice is null;

    public InvoiceLineInput Copy() => new()
    {
        Description = Description, Quantity = Quantity, UnitPrice = UnitPrice, AccountId = AccountId, TaxCodeId = TaxCodeId,
        ItemId = ItemId, Tracking1Id = Tracking1Id, Tracking2Id = Tracking2Id,
    };
}

/// <summary>The invoice form.</summary>
public sealed class InvoiceInput : DocumentInput
{
    public int CustomerId { get; set; }
    public DateTime IssueDate { get; set; }
    public DateTime DueDate { get; set; }
    public string? Reference { get; set; }
    public string? Notes { get; set; }

    public static InvoiceInput New(DateTime today, InvoiceSettings settings, int defaultAccountId) => new()
    {
        IssueDate = today.Date,
        DueDate = today.Date.AddDays(settings.DueDays),
        AmountsIncludeTax = settings.AmountsIncludeTax,
        Lines = [new InvoiceLineInput { Quantity = 1, AccountId = defaultAccountId }],
    };

    public static InvoiceInput From(InvoiceDetail invoice) => new()
    {
        CustomerId = invoice.CustomerId,
        IssueDate = invoice.IssueDate.ToDateTime(TimeOnly.MinValue),
        DueDate = invoice.DueDate.ToDateTime(TimeOnly.MinValue),
        Reference = invoice.Reference,
        Notes = invoice.Notes,
        AmountsIncludeTax = invoice.AmountsIncludeTax,
        Currency = invoice.Currency,
        ExchangeRate = invoice.ExchangeRate,
        Lines = [.. invoice.Lines.Select(l => new InvoiceLineInput
        {
            Description = l.Description,
            Quantity = l.Quantity,
            UnitPrice = l.UnitPrice,
            AccountId = l.AccountId,
            TaxCodeId = l.TaxCodeId,
            ItemId = l.ItemId,
            Tracking1Id = l.Tracking1Id,
            Tracking2Id = l.Tracking2Id,
        })],
    };

    public IReadOnlyList<string> Validate(IReadOnlySet<int> customers, IReadOnlySet<int> accounts, IReadOnlyDictionary<int, TaxCode> taxCodes)
    {
        var errors = new List<string>();
        if (!customers.Contains(CustomerId))
            errors.Add("Choose a customer.");
        if (IssueDate == default || DueDate == default)
            errors.Add("Enter the invoice date and the due date.");
        else if (DueDate.Date < IssueDate.Date)
            errors.Add("The due date can't be before the invoice date.");
        ValidateLines(errors, accounts, taxCodes, "choose the account the income goes to");
        if (errors.Count == 0 && Totals(taxCodes).Total <= 0)
            errors.Add("The invoice total must be more than zero.");
        return errors;
    }
}

/// <summary>
/// What invoices, bills, quotes and purchase orders share: the lines, whether prices include tax, and the currency.
/// </summary>
public abstract class DocumentInput
{
    public bool AmountsIncludeTax { get; set; }

    /// <summary>Another currency's code (USD), or null for the portfolio's own currency.</summary>
    public string? Currency { get; set; }

    /// <summary>Units of <see cref="Currency"/> per one unit of the portfolio's currency (0.65 USD per AUD), as trades use.</summary>
    public decimal? ExchangeRate { get; set; }

    public List<InvoiceLineInput> Lines { get; set; } = [];

    public IEnumerable<InvoiceLineInput> UsedLines => Lines.Where(l => !l.IsBlank);

    public bool IsForeign => Currency is not null;

    /// <summary>The portfolio's own currency (or a blank one) is stored as no currency, with no rate.</summary>
    public void NormaliseCurrency(string homeCurrency)
    {
        Currency = string.IsNullOrWhiteSpace(Currency) ? null : Currency.Trim().ToUpperInvariant();
        if (Currency is null || string.Equals(Currency, homeCurrency, StringComparison.OrdinalIgnoreCase))
        {
            Currency = null;
            ExchangeRate = null;
        }
    }

    protected void ValidateLines(List<string> errors, IReadOnlySet<int> accounts, IReadOnlyDictionary<int, TaxCode> taxCodes, string accountMessage)
    {
        if (Currency is not null && (Currency.Length != 3 || !Currency.All(char.IsAsciiLetter)))
            errors.Add("Enter a three-letter currency code, such as USD.");
        else if (Currency is not null && ExchangeRate is not > 0)
            errors.Add($"Enter the exchange rate: how many {Currency} to one of the portfolio's currency.");

        var lines = UsedLines.ToList();
        if (lines.Count == 0)
            errors.Add("Add at least one line.");
        for (var i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            var n = i + 1;
            if (string.IsNullOrWhiteSpace(l.Description))
                errors.Add($"Line {n}: enter a description.");
            else if (l.Description.Trim().Length > 500)
                errors.Add($"Line {n}: the description is too long.");
            if (l.Quantity is null || l.Quantity == 0)
                errors.Add($"Line {n}: enter the quantity.");
            if (l.UnitPrice is null)
                errors.Add($"Line {n}: enter the price.");
            if (!accounts.Contains(l.AccountId))
                errors.Add($"Line {n}: {accountMessage}.");
            if (l.TaxCodeId is { } code && !taxCodes.ContainsKey(code))
                errors.Add($"Line {n}: choose a tax code from the list.");
        }
    }

    public (decimal Net, decimal Tax, decimal Total) LineAmounts(InvoiceLineInput line, IReadOnlyDictionary<int, TaxCode> taxCodes) =>
        InvoiceRules.Amounts(line.Quantity ?? 0, line.UnitPrice ?? 0,
            line.TaxCodeId is { } id && taxCodes.TryGetValue(id, out var code) ? code : null, AmountsIncludeTax);

    public (decimal Net, decimal Tax, decimal Total) Totals(IReadOnlyDictionary<int, TaxCode> taxCodes)
    {
        decimal net = 0, tax = 0, total = 0;
        foreach (var line in UsedLines)
        {
            var a = LineAmounts(line, taxCodes);
            net += a.Net;
            tax += a.Tax;
            total += a.Total;
        }
        return (net, tax, total);
    }

    /// <summary>The lines as the XML the save procs read (the database is on a version without JSON).</summary>
    public string LinesXml(IReadOnlyDictionary<int, TaxCode> taxCodes)
    {
        static string N(decimal d) => d.ToString(CultureInfo.InvariantCulture);
        var root = new XElement("lines");
        foreach (var line in UsedLines)
        {
            var a = LineAmounts(line, taxCodes);
            root.Add(new XElement("l",
                new XAttribute("d", line.Description!.Trim()),
                new XAttribute("q", N(line.Quantity ?? 0)),
                new XAttribute("p", N(line.UnitPrice ?? 0)),
                new XAttribute("a", line.AccountId),
                new XAttribute("t", line.TaxCodeId ?? 0),
                new XAttribute("n", N(a.Net)),
                new XAttribute("x", N(a.Tax)),
                new XAttribute("g", N(a.Total)),
                line.ItemId is > 0 ? new XAttribute("i", line.ItemId) : null,
                line.Tracking1Id is > 0 ? new XAttribute("t1", line.Tracking1Id) : null,
                line.Tracking2Id is > 0 ? new XAttribute("t2", line.Tracking2Id) : null));
        }
        return root.ToString(SaveOptions.DisableFormatting);
    }
}

/// <summary>The add / edit customer form.</summary>
public sealed class CustomerInput
{
    public int? CustomerId { get; set; }
    public string? Name { get; set; }
    public string? ContactName { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? Address { get; set; }
    public string? TaxNumber { get; set; }
    public bool IsActive { get; set; } = true;

    public static CustomerInput From(Customer c) => new()
    {
        CustomerId = c.CustomerId,
        Name = c.Name,
        ContactName = c.ContactName,
        Email = c.Email,
        Phone = c.Phone,
        Address = c.Address,
        TaxNumber = c.TaxNumber,
        IsActive = c.IsActive,
    };

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Name))
            errors.Add("Enter the customer's name.");
        else if (Name.Trim().Length > 200)
            errors.Add("The name can be at most 200 characters.");
        if (!string.IsNullOrWhiteSpace(Email) && (Email.Trim().Length > 500 || !Email.Contains('@')))
            errors.Add("Check the email address.");
        if ((Address?.Length ?? 0) > 500)
            errors.Add("The address can be at most 500 characters.");
        if ((Phone?.Trim().Length ?? 0) > 50 || (TaxNumber?.Trim().Length ?? 0) > 50)
            errors.Add("The phone number and tax number can be at most 50 characters.");
        return errors;
    }
}

/// <summary>The invoice settings form.</summary>
public sealed class InvoiceSettingsInput
{
    public string? NumberPrefix { get; set; }
    public int NextNumber { get; set; } = 1;
    public int DueDays { get; set; } = 14;
    public bool AmountsIncludeTax { get; set; }
    public string? PaymentDetails { get; set; }
    public string? Footer { get; set; }

    public static InvoiceSettingsInput From(InvoiceSettings s) => new()
    {
        NumberPrefix = s.NumberPrefix,
        NextNumber = s.NextNumber,
        DueDays = s.DueDays,
        AmountsIncludeTax = s.AmountsIncludeTax,
        PaymentDetails = s.PaymentDetails,
        Footer = s.Footer,
    };

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if ((NumberPrefix?.Trim().Length ?? 0) > 20)
            errors.Add("The number prefix can be at most 20 characters.");
        if (NextNumber < 1)
            errors.Add("The next invoice number must be 1 or more.");
        if (DueDays is < 0 or > 365)
            errors.Add("Days to pay must be between 0 and 365.");
        if ((PaymentDetails?.Length ?? 0) > 1000 || (Footer?.Length ?? 0) > 1000)
            errors.Add("Payment details and the footer can be at most 1000 characters each.");
        return errors;
    }
}

/// <summary>The record a payment form.</summary>
public sealed class InvoicePaymentInput
{
    public DateTime Date { get; set; }
    public decimal? Amount { get; set; }
    public int AccountId { get; set; }
    public string? Reference { get; set; }

    /// <summary>For an invoice or bill in another currency: the rate on the day it was paid.</summary>
    public decimal? ExchangeRate { get; set; }
}
