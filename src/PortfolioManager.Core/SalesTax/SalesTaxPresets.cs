namespace PortfolioManager.Core.SalesTax;

/// <summary>A tax code a country usually needs, before it is saved to a portfolio.</summary>
public sealed record TaxCodePreset(string Code, string Name, string Kind, decimal Rate, string? AmountBoxes, string? TaxBox);

/// <summary>
/// The usual sales tax for a country: its name, how often small businesses report it, the
/// return it goes on, and a starting set of tax codes mapped to that return's boxes. They are
/// only a starting point: every code, rate and box can be changed on the Tax codes page, and a
/// country that isn't listed gets a plain set to fill in.
/// </summary>
public sealed record SalesTaxPreset(
    string CountryCode,
    string TaxName,
    string ReturnName,
    int ReportingMonths,
    bool AmountsIncludeTax,
    IReadOnlyList<TaxCodePreset> Codes,
    string? Note = null);

public static class SalesTaxPresets
{
    private const string S = TaxKind.Sales;
    private const string P = TaxKind.Purchases;

    public static readonly IReadOnlyList<SalesTaxPreset> All =
    [
        new("AU", "GST", "Business activity statement (BAS)", 3, true,
        [
            new("GST", "GST on sales", S, 10, "G1", "1A"),
            new("EXP", "GST-free exports", S, 0, "G1,G2", null),
            new("FRE", "GST-free sales", S, 0, "G1,G3", null),
            new("ITS", "Input-taxed sales", S, 0, "G1,G4", null),
            new("CAP", "GST on capital purchases", P, 10, "G10", "1B"),
            new("CAPF", "GST-free capital purchases", P, 0, "G10,G14", null),
            new("GSTP", "GST on other purchases", P, 10, "G11", "1B"),
            new("FREP", "GST-free purchases", P, 0, "G11,G14", null),
            new("ITP", "Purchases for input-taxed sales", P, 0, "G11,G13", null),
        ],
        "G boxes include GST. Leave out anything that isn't a taxable sale or purchase (dividends, share trades, transfers, wages): give it no tax code."),

        new("NZ", "GST", "GST return (GST101A)", 2, true,
        [
            new("GST", "Standard-rated sales", S, 15, "5", "8"),
            new("ZER", "Zero-rated sales", S, 0, "5,6", null),
            new("GSTP", "Standard-rated purchases", P, 15, "11", "12"),
        ],
        "Boxes 5 and 11 include GST. Exempt sales and purchases (like residential rent) get no tax code."),

        new("GB", "VAT", "VAT return", 3, false,
        [
            new("SR", "Standard-rated sales", S, 20, "6", "1"),
            new("RR", "Reduced-rate sales", S, 5, "6", "1"),
            new("ZR", "Zero-rated sales", S, 0, "6", null),
            new("EX", "Exempt sales", S, 0, "6", null),
            new("SRP", "Standard-rated purchases", P, 20, "7", "4"),
            new("RRP", "Reduced-rate purchases", P, 5, "7", "4"),
            new("ZRP", "Zero-rated purchases", P, 0, "7", null),
            new("EXP", "Exempt purchases", P, 0, "7", null),
        ],
        "Boxes 6 and 7 are before VAT. HMRC wants them in whole pounds."),

        new("IE", "VAT", "VAT3 return", 2, false,
        [
            new("S23", "Sales at the standard rate", S, 23, null, "T1"),
            new("S135", "Sales at the reduced rate", S, 13.5m, null, "T1"),
            new("S0", "Zero-rated sales", S, 0, null, null),
            new("P23", "Purchases at the standard rate", P, 23, null, "T2"),
            new("P135", "Purchases at the reduced rate", P, 13.5m, null, "T2"),
            new("P0", "Zero-rated purchases", P, 0, null, null),
        ]),

        new("ZA", "VAT", "VAT201 return", 2, true,
        [
            new("SR", "Standard-rated supplies", S, 15, "1", "4"),
            new("ZR", "Zero-rated supplies", S, 0, "2", null),
            new("ZRE", "Zero-rated exports", S, 0, "2A", null),
            new("EX", "Exempt supplies", S, 0, "3", null),
            new("CAP", "Capital goods and services", P, 15, "14", "14 VAT"),
            new("STP", "Other goods and services", P, 15, "15", "15 VAT"),
        ]),

        new("SG", "GST", "GST F5 return", 3, false,
        [
            new("SR", "Standard-rated supplies", S, 9, "1", "6"),
            new("ZR", "Zero-rated supplies", S, 0, "2", null),
            new("ES", "Exempt supplies", S, 0, "3", null),
            new("TX", "Taxable purchases", P, 9, "5", "7"),
            new("ZP", "Zero-rated purchases", P, 0, "5", null),
        ]),

        new("CA", "GST/HST", "GST/HST return", 3, false,
        [
            new("GST", "Sales with GST", S, 5, "101", "105"),
            new("HST", "Sales with HST (Ontario)", S, 13, "101", "105"),
            new("ZR", "Zero-rated sales", S, 0, "101", null),
            new("GSTP", "Purchases with GST", P, 5, null, "108"),
            new("HSTP", "Purchases with HST (Ontario)", P, 13, null, "108"),
        ],
        "HST rates differ by province: change the HST rate to your province's. Exempt sales get no tax code."),

        new("IN", "GST", "GSTR-3B return", 1, false,
        [
            new("G18", "Taxable sales at 18%", S, 18, "3.1(a)", "3.1(a) tax"),
            new("G5", "Taxable sales at 5%", S, 5, "3.1(a)", "3.1(a) tax"),
            new("ZR", "Zero-rated sales", S, 0, "3.1(b)", null),
            new("NIL", "Nil-rated and exempt sales", S, 0, "3.1(c)", null),
            new("P18", "Purchases at 18%", P, 18, null, "4(A)(5)"),
            new("P5", "Purchases at 5%", P, 5, null, "4(A)(5)"),
        ],
        "The tax is one total: split it into CGST, SGST and IGST on the return."),

        new("US", "Sales tax", "State sales tax return", 3, false,
        [
            new("TAX", "Taxable sales", S, 0, "Gross sales,Taxable sales", "Tax due"),
            new("EXM", "Exempt sales", S, 0, "Gross sales,Exempt sales", null),
        ],
        "Set the Taxable sales rate to your combined state and local rate. Sales tax paid on purchases can't be claimed back, so purchases need no code."),
    ];

    /// <summary>The preset for a country, or a plain one to fill in.</summary>
    public static SalesTaxPreset For(string? countryCode) =>
        All.FirstOrDefault(p => p.CountryCode.Equals(countryCode?.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? new SalesTaxPreset(countryCode?.Trim().ToUpperInvariant() ?? "XX", "Sales tax", "Sales tax return", 3, false,
        [
            new("STD", "Standard-rated sales", S, 0, "Sales", "Tax on sales"),
            new("ZERO", "Zero-rated or exempt sales", S, 0, "Sales", null),
            new("STDP", "Standard-rated purchases", P, 0, "Purchases", "Tax on purchases"),
            new("ZEROP", "Zero-rated or exempt purchases", P, 0, "Purchases", null),
        ],
        "Set the rates to your country's, and rename the boxes to match your return.");

    /// <summary>What the tax is usually called in a country, for menus before a portfolio has set it up.</summary>
    public static string TaxName(string? countryCode) => For(countryCode).TaxName;
}
