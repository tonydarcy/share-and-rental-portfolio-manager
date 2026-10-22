using PortfolioManager.Core.Clients;

namespace PortfolioManager.Core.TaxWork;

/// <summary>The official ATO paper form for a return, and the NAT number to order it by.</summary>
public sealed record PaperForm(string Name, string Nat, string? SupplementaryName = null, string? SupplementaryNat = null);

/// <summary>An ATO mailing address for paper returns: GPO Box 9845 in a capital city.</summary>
public sealed record PaperReturnAddress(string City, string State, string Postcode)
{
    public IReadOnlyList<string> Lines => ["Australian Taxation Office", "GPO Box 9845", $"{City.ToUpperInvariant()} {State} {Postcode}"];
}

/// <summary>One part of the paper return (e.g. the individual return's supplementary section) and its labels.</summary>
public sealed record PaperReturnPart(string Name, IReadOnlyList<(string Section, IReadOnlyList<TaxReturnLabel> Labels)> Sections);

/// <summary>
/// The tax return figures laid out to copy onto the ATO's paper form. The ATO doesn't let the
/// income tax return forms be downloaded or printed from software (they are scanned), so the
/// forms are ordered from its Publications Ordering Service and filled in by hand from this sheet.
/// </summary>
public static class PaperReturn
{
    public const string OrderingServiceUrl = "https://iorder.com.au/";

    public static PaperForm FormFor(string entityType) => entityType switch
    {
        ClientEntityTypes.Company => new("Company tax return", "NAT 0656"),
        ClientEntityTypes.Trust => new("Trust tax return", "NAT 0660"),
        ClientEntityTypes.Partnership => new("Partnership tax return", "NAT 0659"),
        ClientEntityTypes.Smsf => new("Self-managed superannuation fund annual return", "NAT 71226"),
        _ => new("Tax return for individuals", "NAT 2541", "Tax return for individuals (supplementary section)", "NAT 2679"),
    };

    public const string MainPart = "Tax return";
    public const string SupplementaryPart = "Supplementary section";
    public const string BusinessSchedulePart = "Business and professional items schedule";

    /// <summary>
    /// Which part of the individual paper return a label goes on: items 1 to 12 and deductions D1 to D10
    /// are on the main return, items 13 to 24 and D11 onwards on the supplementary section, and P items
    /// on the business and professional items schedule. Other entities have one form.
    /// </summary>
    public static string PartFor(string entityType, string code)
    {
        if (entityType is ClientEntityTypes.Company or ClientEntityTypes.Trust or ClientEntityTypes.Partnership or ClientEntityTypes.Smsf)
            return MainPart;
        if (code.StartsWith('P'))
            return BusinessSchedulePart;
        var deduction = code.StartsWith('D');
        var digits = new string(code.SkipWhile(ch => ch == 'D').TakeWhile(char.IsDigit).ToArray());
        if (!int.TryParse(digits, out var item))
            return MainPart;
        return deduction ? (item >= 11 ? SupplementaryPart : MainPart) : (item >= 13 ? SupplementaryPart : MainPart);
    }

    /// <summary>The labels grouped by part of the form, then by section, in return order.</summary>
    public static IReadOnlyList<PaperReturnPart> Parts(TaxReturnSchedule s)
    {
        string[] order = [MainPart, SupplementaryPart, BusinessSchedulePart];
        return s.Labels
            .GroupBy(l => PartFor(s.EntityType, l.Code))
            .OrderBy(g => Array.IndexOf(order, g.Key))
            .Select(g => new PaperReturnPart(g.Key,
                g.GroupBy(l => l.Section).Select(sg => (sg.Key, (IReadOnlyList<TaxReturnLabel>)sg.ToList())).ToList()))
            .ToList();
    }

    public static readonly IReadOnlyList<PaperReturnAddress> Addresses =
    [
        new("Sydney", "NSW", "2001"),
        new("Melbourne", "VIC", "3001"),
        new("Brisbane", "QLD", "4001"),
        new("Adelaide", "SA", "5001"),
        new("Perth", "WA", "6001"),
        new("Hobart", "TAS", "7001"),
        new("Darwin", "NT", "0801"),
        new("Canberra", "ACT", "2601"),
    ];

    /// <summary>The address in the capital city of the client's state, Sydney when it isn't known (also the address from overseas).</summary>
    public static PaperReturnAddress AddressFor(string? state) =>
        Addresses.FirstOrDefault(a => string.Equals(a.State, state?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? Addresses[0];

    /// <summary>The amount as written on the form: no sign (losses go in the form's LOSS box) and cents shown.</summary>
    public static string FormAmount(decimal amount) => Math.Abs(amount).ToString("#,##0.00", System.Globalization.CultureInfo.InvariantCulture);
}
