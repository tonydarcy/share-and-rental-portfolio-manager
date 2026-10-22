using PortfolioManager.Core.Bills;
using PortfolioManager.Core.Claims;
using PortfolioManager.Core.Invoicing;
using PortfolioManager.Core.SalesTax;

namespace PortfolioManager.Core.Reading;

/// <summary>Turns what was read into a draft bill or an expense claim for the person to check. Nothing here posts.</summary>
public static class ReadingDrafts
{
    /// <summary>The supplier whose name (or ABN) matches what was read, if there is one.</summary>
    public static Supplier? MatchSupplier(IEnumerable<Supplier> suppliers, ReadResult read)
    {
        var list = suppliers.Where(s => s.IsActive).ToList();
        if (read.TaxNumber is { Length: > 0 } abn && list.FirstOrDefault(s => s.TaxNumber?.Replace(" ", "") == abn) is { } byAbn)
            return byAbn;
        if (read.Payee is not { Length: > 0 } payee)
            return null;
        return list.FirstOrDefault(s => string.Equals(s.Name, payee, StringComparison.OrdinalIgnoreCase))
            ?? list.FirstOrDefault(s => payee.Contains(s.Name, StringComparison.OrdinalIgnoreCase) || s.Name.Contains(payee, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The tax code for the tax read: none when there was no tax, else the supplier's usual one or the first purchases code with a rate.</summary>
    public static int? TaxCodeFor(ReadResult read, IEnumerable<TaxCode> purchaseCodes, int? usual) =>
        read.Tax is not > 0 ? null : usual ?? purchaseCodes.Where(c => !c.IsSales && c.Rate > 0).Select(c => (int?)c.TaxCodeId).FirstOrDefault();

    public static BillInput Bill(ReadResult read, int supplierId, int dueDays, int accountId, int? taxCodeId, DateTime today, string homeCurrency)
    {
        var date = read.Date?.ToDateTime(TimeOnly.MinValue) ?? today.Date;
        var currency = read.Currency is { } c && !string.Equals(c, homeCurrency, StringComparison.OrdinalIgnoreCase) ? c : null;
        return new BillInput
        {
            SupplierId = supplierId,
            Kind = BillKinds.Bill,
            Number = read.Reference is { Length: <= 60 } r ? r : null,
            IssueDate = date,
            DueDate = read.DueDate?.ToDateTime(TimeOnly.MinValue) is { } due && due >= date ? due : date.AddDays(dueDays),
            AmountsIncludeTax = true,
            Currency = currency,
            Notes = Note(read),
            Lines = [new InvoiceLineInput
            {
                Description = Describe(read), Quantity = 1, UnitPrice = read.Total ?? 0, AccountId = accountId, TaxCodeId = taxCodeId,
            }],
        };
    }

    public static ClaimInput Claim(ReadResult read, string claimant, int accountId, int? taxCodeId, DateTime today) => new()
    {
        Claimant = claimant,
        Date = read.Date?.ToDateTime(TimeOnly.MinValue) ?? today.Date,
        Payee = read.Payee is { Length: <= 200 } p ? p : null,
        Description = Describe(read),
        AccountId = accountId,
        TaxCodeId = taxCodeId,
        Amount = read.Total ?? 0,
        Tax = taxCodeId is null ? null : read.Tax,
    };

    private static string Describe(ReadResult read)
    {
        var text = read.Description ?? (read.Payee is { } p ? $"{p}{(read.Reference is { } r ? " " + r : "")}" : "As per the attached document");
        return text.Length > 500 ? text[..500] : text;
    }

    private static string Note(ReadResult read) =>
        $"Read from the attached document ({(read.Source == "Claude" ? "by Claude" : "by the built-in reader")}). Check the amounts before approving.";
}
