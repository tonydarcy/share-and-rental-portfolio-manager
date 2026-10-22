namespace PortfolioManager.Core.SalesTax;

/// <summary>Whether a tax code is used on income (sales) or spending (purchases).</summary>
public static class TaxKind
{
    public const string Sales = "Sales";
    public const string Purchases = "Purchases";

    public static readonly IReadOnlyList<string> All = [Sales, Purchases];
}

/// <summary>
/// One of the portfolio's tax codes (tblTaxCodes). Rate is a percentage. AmountBoxes are the
/// boxes of the tax return its amounts are added to (e.g. "G1,G2" on an Australian BAS), and
/// TaxBox the box its tax is added to ("1A"). AmountIncludesTax says whether those amount boxes
/// want the amount with tax (Australia, New Zealand) or before tax (UK, Singapore).
/// </summary>
public sealed record TaxCode(
    int TaxCodeId,
    string Code,
    string Name,
    string Kind,
    decimal Rate,
    string? AmountBoxes,
    string? TaxBox,
    bool AmountIncludesTax,
    bool IsActive,
    int SortOrder,
    bool InUse = false)
{
    public bool IsSales => Kind == TaxKind.Sales;

    public string Display => $"{Code} - {Name} ({Rate.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}%)";

    /// <summary>The amount boxes as a list, e.g. ["G1", "G2"].</summary>
    public IReadOnlyList<string> AmountBoxList => SplitBoxes(AmountBoxes);

    public static IReadOnlyList<string> SplitBoxes(string? boxes) =>
        (boxes ?? "").Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}

/// <summary>Working out the tax in an amount, rounded to the cent.</summary>
public static class TaxMath
{
    /// <summary>The tax inside an amount that includes it: 110 at 10% has 10 of tax.</summary>
    public static decimal TaxInGross(decimal gross, decimal rate) =>
        rate <= 0 ? 0 : decimal.Round(gross * rate / (100 + rate), 2, MidpointRounding.AwayFromZero);

    /// <summary>The tax to add to an amount before tax: 100 at 10% has 10 of tax.</summary>
    public static decimal TaxOnNet(decimal net, decimal rate) =>
        rate <= 0 ? 0 : decimal.Round(net * rate / 100, 2, MidpointRounding.AwayFromZero);
}

/// <summary>The fields of the add / edit tax code form.</summary>
public sealed class TaxCodeInput
{
    public int? TaxCodeId { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = TaxKind.Sales;
    public decimal? Rate { get; set; }
    public string? AmountBoxes { get; set; }
    public string? TaxBox { get; set; }
    public bool AmountIncludesTax { get; set; } = true;
    public bool IsActive { get; set; } = true;
    public int? SortOrder { get; set; }

    public IReadOnlyList<string> Validate(IReadOnlyList<TaxCode> existing)
    {
        var errors = new List<string>();
        var code = Code?.Trim() ?? "";
        if (code.Length == 0)
            errors.Add("Enter a short code, e.g. GST.");
        else if (code.Length > 10)
            errors.Add("The code can be at most 10 characters.");
        else if (existing.Any(c => c.Code.Equals(code, StringComparison.OrdinalIgnoreCase) && c.TaxCodeId != TaxCodeId))
            errors.Add($"There is already a tax code {code.ToUpperInvariant()}.");

        var name = Name?.Trim() ?? "";
        if (name.Length == 0)
            errors.Add("Enter the name of the tax code.");
        else if (name.Length > 100)
            errors.Add("The name can be at most 100 characters.");

        if (!TaxKind.All.Contains(Kind))
            errors.Add("Choose sales or purchases.");
        if (Rate is null or < 0 or > 100)
            errors.Add("Enter the rate as a percentage from 0 to 100.");
        else if (decimal.Round(Rate.Value, 4) != Rate.Value)
            errors.Add("The rate can have at most 4 decimal places.");
        if ((AmountBoxes?.Trim().Length ?? 0) > 100)
            errors.Add("The amount boxes can be at most 100 characters.");
        if ((TaxBox?.Trim().Length ?? 0) > 20)
            errors.Add("The tax box can be at most 20 characters.");
        return errors;
    }

    public static TaxCodeInput From(TaxCode c) => new()
    {
        TaxCodeId = c.TaxCodeId,
        Code = c.Code,
        Name = c.Name,
        Kind = c.Kind,
        Rate = c.Rate,
        AmountBoxes = c.AmountBoxes,
        TaxBox = c.TaxBox,
        AmountIncludesTax = c.AmountIncludesTax,
        IsActive = c.IsActive,
        SortOrder = c.SortOrder,
    };
}

/// <summary>
/// A portfolio's sales tax setup (tblSalesTaxSetup): what the tax is called, how many months
/// each return covers, and the ledger account the tax is posted to.
/// </summary>
public sealed record SalesTaxSetup(string TaxName, int ReportingMonths, int ControlAccountId, string ControlAccountName)
{
    public static readonly IReadOnlyList<int> PeriodChoices = [1, 2, 3, 6, 12];

    public static string PeriodLabel(int months) => months switch
    {
        1 => "Monthly",
        2 => "Every two months",
        3 => "Quarterly",
        6 => "Every six months",
        12 => "Yearly",
        _ => $"Every {months} months",
    };
}

/// <summary>The fields of the setup form. A blank ControlAccountId adds an account for the tax.</summary>
public sealed class SalesTaxSetupInput
{
    public string TaxName { get; set; } = "";
    public int ReportingMonths { get; set; } = 3;
    public int? ControlAccountId { get; set; }

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        var name = TaxName?.Trim() ?? "";
        if (name.Length == 0)
            errors.Add("Enter what the tax is called, e.g. GST or VAT.");
        else if (name.Length > 30)
            errors.Add("The tax name can be at most 30 characters.");
        if (!SalesTaxSetup.PeriodChoices.Contains(ReportingMonths))
            errors.Add("Choose how often the tax is reported.");
        return errors;
    }
}
