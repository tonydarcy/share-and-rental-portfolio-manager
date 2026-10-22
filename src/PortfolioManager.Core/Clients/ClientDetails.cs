using System.Text.RegularExpressions;

namespace PortfolioManager.Core.Clients;

/// <summary>The kinds of entity a set of financial statements can be prepared for.</summary>
public static class ClientEntityTypes
{
    public const string Individual = "Individual";
    public const string Partnership = "Partnership";
    public const string Company = "Company";
    public const string Trust = "Trust";
    public const string Smsf = "SMSF";

    public static readonly IReadOnlyList<string> All = [Individual, Partnership, Company, Trust, Smsf];

    public static string Describe(string type) => type switch
    {
        Smsf => "Self-managed super fund",
        _ => type,
    };

    /// <summary>
    /// A starting guess from the portfolio's own type (tblUsers.strEntityType: Individual,
    /// Company, Superfund or Other) until the client details are saved.
    /// </summary>
    public static string FromPortfolioType(string? portfolioType) => portfolioType?.Trim().ToLowerInvariant() switch
    {
        "company" => Company,
        "superfund" or "super fund" or "smsf" => Smsf,
        "trust" => Trust,
        "partnership" => Partnership,
        _ => Individual,
    };

    public static bool HasTrustee(string type) => type is Trust or Smsf;
}

public static class TrusteeTypes
{
    public const string Individual = "Individual";
    public const string Corporate = "Corporate";
    public static readonly IReadOnlyList<string> All = [Individual, Corporate];
}

public static class OfficeholderRoles
{
    public const string Director = "Director";
    public const string Secretary = "Secretary";
    public const string Trustee = "Trustee";
    public const string TrusteeDirector = "Director of trustee company";
    public const string Partner = "Partner";
    public const string Member = "Member";

    public static readonly IReadOnlyList<string> All = [Director, Secretary, Trustee, TrusteeDirector, Partner, Member];
}

public static class AustralianStates
{
    public static readonly IReadOnlyList<string> All = ["ACT", "NSW", "NT", "QLD", "SA", "TAS", "VIC", "WA"];
}

public sealed record Officeholder(string Name, string Role);

/// <summary>A postal address as the statements print it.</summary>
public sealed record PostalAddress(string? Line1, string? Line2, string? Suburb, string? State, string? Postcode)
{
    /// <summary>The non-empty lines, with suburb, state and postcode on the last one.</summary>
    public IReadOnlyList<string> Lines
    {
        get
        {
            var lines = new List<string>();
            if (!string.IsNullOrWhiteSpace(Line1)) lines.Add(Line1.Trim());
            if (!string.IsNullOrWhiteSpace(Line2)) lines.Add(Line2.Trim());
            var last = string.Join(" ", new[] { Suburb, State, Postcode }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim()));
            if (last.Length > 0) lines.Add(last);
            return lines;
        }
    }

    public string OneLine => string.Join(", ", Lines);
}

/// <summary>
/// A portfolio's client details (uspGetClientDetails). Until they are saved, the legal name and
/// entity type are taken from the portfolio itself.
/// </summary>
public sealed record ClientDetails(
    int PortfolioId,
    string PortfolioName,
    string PortfolioEntityType,
    bool Saved,
    string LegalName,
    string EntityType,
    string? Abn,
    string? Acn,
    string? TrusteeName,
    string? TrusteeType,
    string? TrusteeAcn,
    PostalAddress Address,
    string ReportingBasis,
    DateTime? Updated,
    IReadOnlyList<Officeholder> Officeholders)
{
    public const string DefaultReportingBasis = "special purpose financial report";

    public string AbnDisplay => BusinessNumbers.FormatAbn(Abn);
    public string AcnDisplay => BusinessNumbers.FormatAcn(Acn);
    public string TrusteeAcnDisplay => BusinessNumbers.FormatAcn(TrusteeAcn);
    public bool HasTrustee => ClientEntityTypes.HasTrustee(EntityType);
    public bool CorporateTrustee => HasTrustee && TrusteeType == TrusteeTypes.Corporate;

    public static ClientDetails Unsaved(int portfolioId, string portfolioName, string portfolioType) => new(
        portfolioId, portfolioName, portfolioType, false, portfolioName,
        ClientEntityTypes.FromPortfolioType(portfolioType), null, null, null, null, null,
        new PostalAddress(null, null, null, null, null), DefaultReportingBasis, null, []);
}

/// <summary>One row of the Clients page (uspGetClientList).</summary>
public sealed record ClientListItem(
    int PortfolioId,
    string PortfolioName,
    string PortfolioEntityType,
    bool Saved,
    string LegalName,
    string EntityType,
    string? Abn,
    string? Acn,
    DateTime? Updated)
{
    public string AbnDisplay => BusinessNumbers.FormatAbn(Abn);
}

public sealed class OfficeholderInput
{
    public string? Name { get; set; }
    public string? Role { get; set; }
    public bool IsBlank => string.IsNullOrWhiteSpace(Name);
}

/// <summary>The client details form.</summary>
public sealed class ClientDetailsInput
{
    public const int MaxOfficeholders = 20;

    public string LegalName { get; set; } = "";
    public string EntityType { get; set; } = ClientEntityTypes.Individual;
    public string? Abn { get; set; }
    public string? Acn { get; set; }
    public string? TrusteeName { get; set; }
    public string? TrusteeType { get; set; }
    public string? TrusteeAcn { get; set; }
    public string? AddressLine1 { get; set; }
    public string? AddressLine2 { get; set; }
    public string? Suburb { get; set; }
    public string? State { get; set; }
    public string? Postcode { get; set; }
    public string? ReportingBasis { get; set; }
    public List<OfficeholderInput> Officeholders { get; set; } = [];

    public bool IsCompany => EntityType == ClientEntityTypes.Company;
    public bool HasTrustee => ClientEntityTypes.HasTrustee(EntityType);

    /// <summary>What gets saved: digits only for the numbers, and only the fields the entity type uses.</summary>
    public string? CleanAbn => BusinessNumbers.Clean(Abn);
    public string? CleanAcn => IsCompany ? BusinessNumbers.Clean(Acn) : null;
    public string? CleanTrusteeName => HasTrustee ? Blank(TrusteeName) : null;
    public string? CleanTrusteeType => HasTrustee ? Blank(TrusteeType) : null;
    public string? CleanTrusteeAcn => HasTrustee && TrusteeType == TrusteeTypes.Corporate ? BusinessNumbers.Clean(TrusteeAcn) : null;
    public string CleanReportingBasis => Blank(ReportingBasis) ?? ClientDetails.DefaultReportingBasis;

    public IReadOnlyList<Officeholder> UsedOfficeholders =>
        Officeholders.Where(o => !o.IsBlank).Select(o => new Officeholder(o.Name!.Trim(), o.Role?.Trim() ?? "")).ToList();

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        var name = LegalName?.Trim() ?? "";
        if (name.Length == 0)
            errors.Add("Enter the client's legal name.");
        else if (name.Length > 500)
            errors.Add("The legal name can be at most 500 characters.");

        if (!ClientEntityTypes.All.Contains(EntityType))
            errors.Add("Choose the entity type.");

        if (CleanAbn is { } abn && !BusinessNumbers.IsValidAbn(abn))
            errors.Add("The ABN isn't valid. It has 11 digits and its check digits must add up (check it on ABN Lookup).");

        if (CleanAcn is { } acn)
        {
            if (!BusinessNumbers.IsValidAcn(acn))
                errors.Add("The ACN isn't valid. It has 9 digits and its last digit is a check digit.");
            else if (CleanAbn is { } companyAbn && BusinessNumbers.IsValidAbn(companyAbn) && !BusinessNumbers.AbnMatchesAcn(companyAbn, acn))
                errors.Add("A company's ABN is its ACN with two digits in front, and these don't match.");
        }

        if (HasTrustee)
        {
            if (CleanTrusteeType is { } tt && !TrusteeTypes.All.Contains(tt))
                errors.Add("Choose whether the trustee is an individual or a company.");
            if ((CleanTrusteeName?.Length ?? 0) > 500)
                errors.Add("The trustee's name can be at most 500 characters.");
            if (CleanTrusteeAcn is { } tacn && !BusinessNumbers.IsValidAcn(tacn))
                errors.Add("The trustee company's ACN isn't valid.");
        }

        errors.AddRange(AddressErrors(AddressLine1, AddressLine2, Suburb, State, Postcode));

        if ((Blank(ReportingBasis)?.Length ?? 0) > 500)
            errors.Add("The reporting basis wording can be at most 500 characters.");

        var people = UsedOfficeholders;
        if (people.Count > MaxOfficeholders)
            errors.Add($"List at most {MaxOfficeholders} directors, trustees, partners or members.");
        if (people.Any(p => p.Name.Length > 200))
            errors.Add("An officeholder's name can be at most 200 characters.");
        if (people.Any(p => !OfficeholderRoles.All.Contains(p.Role)))
            errors.Add("Choose a role for each director, trustee, partner or member.");
        return errors;
    }

    public static ClientDetailsInput From(ClientDetails d) => new()
    {
        LegalName = d.LegalName,
        EntityType = d.EntityType,
        Abn = d.AbnDisplay,
        Acn = d.AcnDisplay,
        TrusteeName = d.TrusteeName,
        TrusteeType = d.TrusteeType,
        TrusteeAcn = d.TrusteeAcnDisplay,
        AddressLine1 = d.Address.Line1,
        AddressLine2 = d.Address.Line2,
        Suburb = d.Address.Suburb,
        State = d.Address.State,
        Postcode = d.Address.Postcode,
        ReportingBasis = d.ReportingBasis,
        Officeholders = d.Officeholders.Select(o => new OfficeholderInput { Name = o.Name, Role = o.Role }).ToList(),
    };

    internal static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    internal static IEnumerable<string> AddressErrors(string? line1, string? line2, string? suburb, string? state, string? postcode)
    {
        if ((Blank(line1)?.Length ?? 0) > 200 || (Blank(line2)?.Length ?? 0) > 200)
            yield return "Each address line can be at most 200 characters.";
        if ((Blank(suburb)?.Length ?? 0) > 100)
            yield return "The suburb can be at most 100 characters.";
        if (Blank(state) is { } s && !AustralianStates.All.Contains(s))
            yield return "Choose the state or territory.";
        if (Blank(postcode) is { } p && !Regex.IsMatch(p, @"^\d{4}$"))
            yield return "The postcode has 4 digits.";
    }
}

/// <summary>The preparer's practice, kept per login (uspGetPracticeDetails).</summary>
public sealed record PracticeDetails(
    string PracticeName,
    string PreparerName,
    string? TaxAgentNumber,
    string? ProfessionalBody,
    PostalAddress Address,
    string? Phone,
    string? Email);

public sealed class PracticeDetailsInput
{
    public string PracticeName { get; set; } = "";
    public string PreparerName { get; set; } = "";
    public string? TaxAgentNumber { get; set; }
    public string? ProfessionalBody { get; set; }
    public string? AddressLine1 { get; set; }
    public string? AddressLine2 { get; set; }
    public string? Suburb { get; set; }
    public string? State { get; set; }
    public string? Postcode { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }

    public string? CleanTaxAgentNumber => BusinessNumbers.Clean(TaxAgentNumber);

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        var practice = PracticeName?.Trim() ?? "";
        var preparer = PreparerName?.Trim() ?? "";
        if (practice.Length == 0)
            errors.Add("Enter the practice name.");
        else if (practice.Length > 200)
            errors.Add("The practice name can be at most 200 characters.");
        if (preparer.Length == 0)
            errors.Add("Enter the preparer's name.");
        else if (preparer.Length > 200)
            errors.Add("The preparer's name can be at most 200 characters.");

        if (CleanTaxAgentNumber is { } tan && !Regex.IsMatch(tan, @"^\d{8}$"))
            errors.Add("The registered tax agent number has 8 digits.");
        if ((ClientDetailsInput.Blank(ProfessionalBody)?.Length ?? 0) > 100)
            errors.Add("The professional body can be at most 100 characters.");

        errors.AddRange(ClientDetailsInput.AddressErrors(AddressLine1, AddressLine2, Suburb, State, Postcode));

        if ((ClientDetailsInput.Blank(Phone)?.Length ?? 0) > 50)
            errors.Add("The phone number can be at most 50 characters.");
        if (ClientDetailsInput.Blank(Email) is { } email && (email.Length > 200 || !Regex.IsMatch(email, @"^[^@\s]+@[^@\s]+\.[^@\s]+$")))
            errors.Add("Enter a valid email address.");
        return errors;
    }

    public static PracticeDetailsInput From(PracticeDetails p) => new()
    {
        PracticeName = p.PracticeName,
        PreparerName = p.PreparerName,
        TaxAgentNumber = p.TaxAgentNumber,
        ProfessionalBody = p.ProfessionalBody,
        AddressLine1 = p.Address.Line1,
        AddressLine2 = p.Address.Line2,
        Suburb = p.Address.Suburb,
        State = p.Address.State,
        Postcode = p.Address.Postcode,
        Phone = p.Phone,
        Email = p.Email,
    };
}
