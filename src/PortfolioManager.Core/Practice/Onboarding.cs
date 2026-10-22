using System.Data;
using System.Security.Cryptography;
using System.Xml.Linq;
using Dapper;
using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Clients;
using PortfolioManager.Core.Data;
using PortfolioManager.Core.Portfolios;

namespace PortfolioManager.Core.Practice;

public static class OnboardingStatuses
{
    public const string Sent = "Sent", Accepted = "Accepted", SetUp = "SetUp", Cancelled = "Cancelled";

    public static string Label(string s) => s switch
    {
        Sent => "Waiting for the client",
        Accepted => "Accepted: ready to set up",
        SetUp => "Set up",
        _ => "Cancelled",
    };
}

/// <summary>What the client fills in on the onboarding form.</summary>
public sealed class OnboardingDetails
{
    public string? LegalName { get; set; }
    public string? Abn { get; set; }
    public string? Acn { get; set; }
    public string? TrusteeName { get; set; }
    public string? ContactName { get; set; }
    public string? Phone { get; set; }
    public string? AddressLine1 { get; set; }
    public string? AddressLine2 { get; set; }
    public string? Suburb { get; set; }
    public string? State { get; set; }
    public string? Postcode { get; set; }

    /// <summary>One person a line: "Name, role" (directors, trustees, members, partners).</summary>
    public string? People { get; set; }
    public string? Notes { get; set; }

    private static readonly string[] Fields =
        [nameof(LegalName), nameof(Abn), nameof(Acn), nameof(TrusteeName), nameof(ContactName), nameof(Phone), nameof(AddressLine1),
         nameof(AddressLine2), nameof(Suburb), nameof(State), nameof(Postcode), nameof(People), nameof(Notes)];

    public IReadOnlyList<string> Validate(string entityType)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(LegalName))
            errors.Add("Enter the full legal name.");
        if (BusinessNumbers.Clean(Abn) is { } abn && !BusinessNumbers.IsValidAbn(abn))
            errors.Add("That ABN doesn't look right. Check the 11 digits.");
        if (entityType == ClientEntityTypes.Company && string.IsNullOrWhiteSpace(Acn))
            errors.Add("Enter the company's ACN.");
        foreach (var f in Fields)
            if ((Get(f)?.Length ?? 0) > (f is nameof(People) or nameof(Notes) ? 2000 : 500))
                errors.Add("One of the answers is too long.");
        return errors.Distinct().ToList();
    }

    public string ToXml() => new XElement("details", Fields.Select(f => Get(f) is { Length: > 0 } v ? new XElement(f, v.Trim()) : null)).ToString(SaveOptions.DisableFormatting);

    public static OnboardingDetails FromXml(string? xml)
    {
        var d = new OnboardingDetails();
        if (string.IsNullOrWhiteSpace(xml))
            return d;
        foreach (var e in XElement.Parse(xml).Elements())
            if (Fields.Contains(e.Name.LocalName))
                typeof(OnboardingDetails).GetProperty(e.Name.LocalName)!.SetValue(d, e.Value);
        return d;
    }

    private string? Get(string field) => (string?)typeof(OnboardingDetails).GetProperty(field)!.GetValue(this);

    /// <summary>The client details to save with the new portfolio.</summary>
    public ClientDetailsInput ToClientDetails(string entityType) => new()
    {
        LegalName = LegalName?.Trim() ?? "",
        EntityType = entityType,
        Abn = Abn,
        Acn = entityType == ClientEntityTypes.Company ? Acn : null,
        TrusteeName = ClientEntityTypes.HasTrustee(entityType) ? TrusteeName : null,
        TrusteeType = ClientEntityTypes.HasTrustee(entityType) && !string.IsNullOrWhiteSpace(TrusteeName)
            ? (TrusteeName.Contains("Pty", StringComparison.OrdinalIgnoreCase) || TrusteeName.Contains("Ltd", StringComparison.OrdinalIgnoreCase)
                ? TrusteeTypes.Corporate : TrusteeTypes.Individual)
            : null,
        AddressLine1 = AddressLine1,
        AddressLine2 = AddressLine2,
        Suburb = Suburb,
        State = AustralianStates.All.Contains(State?.Trim().ToUpperInvariant() ?? "") ? State!.Trim().ToUpperInvariant() : null,
        Postcode = Postcode,
        Officeholders = (People ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(ClientDetailsInput.MaxOfficeholders)
            .Select(line => line.Split(',', 2, StringSplitOptions.TrimEntries))
            .Select(p => new OfficeholderInput { Name = p[0], Role = p.Length > 1 ? p[1] : "" })
            .ToList(),
    };
}

public sealed record Onboarding(int Id, int PracticeUserId, string Token, string ClientName, string ClientEmail, string EntityType, string Letter,
    string Status, DateTime Created, DateTime? Accepted, string? AcceptedName, string? AcceptedIp, string? DetailsXml, int? PortfolioId)
{
    public OnboardingDetails Details => OnboardingDetails.FromXml(DetailsXml);
}

public static class EngagementLetters
{
    /// <summary>A starting letter for the entity type, for the accountant to change before sending. Tony to check the wording.</summary>
    public static string Default(string entityType, PracticeDetails? practice, string clientName)
    {
        var firm = practice?.PracticeName is { Length: > 0 } p ? p : "our practice";
        var work = entityType switch
        {
            ClientEntityTypes.Smsf => "prepare the fund's financial statements, member statements and SMSF annual return, and arrange the independent audit",
            ClientEntityTypes.Company => "prepare the company's financial statements and income tax return, and keep the franking account and Division 7A loans in order",
            ClientEntityTypes.Trust => "prepare the trust's financial statements, distribution minutes and trust tax return",
            ClientEntityTypes.Partnership => "prepare the partnership's financial statements and partnership tax return",
            _ => "prepare your income tax return, including your investment income, capital gains and deductions",
        };
        return $"""
            Dear {clientName},

            Thank you for choosing {firm}. This letter sets out the work we will do for you and what we need from you.

            The work: we will {work} each year, from the records you give us and the records kept in this portfolio. We do not audit or review your records, so we can't promise they are free of error or fraud.

            What we need from you: complete and accurate records, answers to our questions in reasonable time, and to tell us of anything that may affect your tax. You are responsible for the information in your returns, and for keeping your records for five years.

            Our fees are charged as agreed with you, and are due within 14 days of each invoice.

            We are registered tax agents and follow the Code of Professional Conduct in the Tax Agent Services Act 2009. We keep your information private and use it only for this work, unless the law requires otherwise.

            This engagement continues until either of us ends it in writing.

            By accepting below you confirm you have read and agree to these terms.

            {practice?.PreparerName ?? ""}
            {firm}
            """;
    }
}

/// <summary>
/// Onboarding a new client (2026-10-21-16-practice.sql): an engagement letter and details form sent by a link, then the
/// portfolio, client details, starting accounts and the client's access set up from the answers.
/// </summary>
public sealed class OnboardingService(IDbConnectionFactory db, PortfolioService portfolios, ClientService clients, ChartOfAccountsService chart)
{
    private const string Select = """
        SELECT iOnboardingID AS Id, iPracticeUserID AS PracticeUserId, strToken AS Token, strClientName AS ClientName, strClientEmail AS ClientEmail,
               strEntityType AS EntityType, strLetter AS Letter, strStatus AS Status, dtmCreated AS Created, dtmAccepted AS Accepted,
               strAcceptedName AS AcceptedName, strAcceptedIp AS AcceptedIp, strDetailsXml AS DetailsXml, iPortfolioID AS PortfolioId
        FROM tblOnboarding
        """;

    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    /// <summary>The login's own onboardings, newest first. Empty until the migration has been run.</summary>
    public async Task<IReadOnlyList<Onboarding>> ListAsync(int practiceUserId)
    {
        await using var con = db.Create();
        try
        {
            return (await con.QueryAsync<Onboarding>(Select + " WHERE iPracticeUserID = @practiceUserId ORDER BY dtmCreated DESC", new { practiceUserId })).ToList();
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208)
        {
            return [];
        }
    }

    public async Task<Onboarding?> GetByTokenAsync(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length != 64)
            return null;
        await using var con = db.Create();
        return await con.QuerySingleOrDefaultAsync<Onboarding>(Select + " WHERE strToken = @token", new { token });
    }

    public async Task<(ProcResult Result, string? Token)> CreateAsync(int practiceUserId, string clientName, string clientEmail, string entityType, string letter)
    {
        var token = NewToken();
        await using var con = db.Create();
        var row = await con.QuerySingleAsync<(string Result, int? Id)>("uspAddOnboarding", new
        {
            iPracticeUserID = practiceUserId, strToken = token, strClientName = clientName?.Trim(), strClientEmail = clientEmail?.Trim(),
            strEntityType = entityType, strLetter = letter,
        }, commandType: CommandType.StoredProcedure);
        var result = ProcResult.From(row.Result, "Success", row.Id);
        return (result, result.Ok ? token : null);
    }

    public async Task<ProcResult> AcceptAsync(string token, string acceptedName, string? ip, OnboardingDetails details)
    {
        await using var con = db.Create();
        return ProcResult.From(await con.ExecuteScalarAsync<string>("uspAcceptOnboarding",
            new { strToken = token, strAcceptedName = acceptedName?.Trim(), strAcceptedIp = ip, strDetailsXml = details.ToXml() },
            commandType: CommandType.StoredProcedure), "Success");
    }

    public async Task<ProcResult> CancelAsync(int practiceUserId, int id)
    {
        await using var con = db.Create();
        return ProcResult.From(await con.ExecuteScalarAsync<string>("uspCancelOnboarding",
            new { iPracticeUserID = practiceUserId, iOnboardingID = id }, commandType: CommandType.StoredProcedure), "Success");
    }

    /// <summary>The portfolio type (tblUsers.strEntityType) for a client entity type.</summary>
    public static string PortfolioType(string entityType) => entityType switch
    {
        ClientEntityTypes.Smsf => "Superfund",
        ClientEntityTypes.Company => "Company",
        ClientEntityTypes.Individual => "Individual",
        _ => "Other",
    };

    /// <summary>Accounts each entity type needs that the shared chart doesn't have. Tony to check the numbers and names.</summary>
    public static IReadOnlyList<(int Number, string Name, string Type)> StarterAccounts(string entityType) => entityType switch
    {
        ClientEntityTypes.Company =>
        [
            (3110, "Share Capital", "Equity"), (3300, "Dividends Paid", "Equity"), (2800, "Income Tax Payable", "Liability"),
            (5100, "Accounting Fees", "Expense"), (5200, "ASIC Fees", "Expense"), (5900, "Income Tax Expense", "Expense"),
        ],
        ClientEntityTypes.Trust =>
        [
            (3110, "Settled Sum", "Equity"), (3300, "Distributions to Beneficiaries", "Equity"), (2700, "Beneficiary Entitlements Payable", "Liability"),
            (5100, "Accounting Fees", "Expense"),
        ],
        ClientEntityTypes.Partnership =>
        [
            (3110, "Partners' Capital", "Equity"), (3300, "Partners' Drawings", "Equity"), (5100, "Accounting Fees", "Expense"),
        ],
        ClientEntityTypes.Smsf =>
        [
            (4700, "Employer Contributions", "Revenue"), (4710, "Member Contributions", "Revenue"), (4720, "Rollovers In", "Revenue"),
            (5600, "Pensions Paid", "Expense"), (5610, "Lump Sums Paid", "Expense"), (5100, "Accounting Fees", "Expense"),
            (5110, "Audit Fees", "Expense"), (5120, "ATO Supervisory Levy", "Expense"), (5130, "ASIC Fees", "Expense"),
            (2800, "Income Tax Payable", "Liability"),
        ],
        _ => [(5100, "Accounting Fees", "Expense"), (5300, "Investment Interest Expense", "Expense")],
    };

    /// <summary>
    /// Sets up an accepted client: a new portfolio owned by the accountant, its client details, the starting accounts,
    /// and access for the client's email (an invitation if they haven't signed up). Returns the new portfolio's id.
    /// </summary>
    public async Task<ProcResult> SetUpAsync(int practiceUserId, Onboarding o, PortfolioAccess clientAccess)
    {
        if (o.PracticeUserId != practiceUserId || o.Status != OnboardingStatuses.Accepted)
            return ProcResult.Failed("That client hasn't accepted yet, or is already set up.");
        var details = o.Details;
        var name = (details.LegalName ?? o.ClientName).Trim();
        if (name.Length > 100)
            name = name[..100];
        await portfolios.CreateAsync(practiceUserId, name, PortfolioType(o.EntityType));
        var created = (await portfolios.GetAccessibleAsync(practiceUserId))
            .Where(p => p.Name == name && p.Access == PortfolioAccess.Full)
            .MaxBy(p => p.PortfolioId);
        if (created is null)
            return ProcResult.Failed("The portfolio couldn't be created.");
        var id = created.PortfolioId;

        var saved = await clients.SaveAsync(id, practiceUserId, details.ToClientDetails(o.EntityType));
        var problems = new List<string>();
        if (!saved.Ok)
            problems.Add($"The client details weren't saved ({saved.Error}); add them on Client details.");
        var existing = (await chart.GetAccountsAsync(id)).Select(a => a.Number).ToHashSet();
        foreach (var (number, accountName, type) in StarterAccounts(o.EntityType).Where(a => !existing.Contains(a.Number)))
            await chart.AddAsync(id, new AccountInput { Number = number, Name = accountName, Type = type });
        var share = await portfolios.ShareAsync(practiceUserId, id, o.ClientEmail, clientAccess);
        if (!share.Result.Ok)
            problems.Add($"The client couldn't be given access ({share.Result.Error}); share it on Share this portfolio.");

        await using var con = db.Create();
        await con.ExecuteScalarAsync<string>("uspCompleteOnboarding", new { iPracticeUserID = practiceUserId, iOnboardingID = o.Id, iPortfolioID = id },
            commandType: CommandType.StoredProcedure);
        return problems.Count == 0 ? ProcResult.Success(id) : new ProcResult(true, string.Join(" ", problems), id);
    }
}
