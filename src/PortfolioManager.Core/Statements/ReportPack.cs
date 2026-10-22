using PortfolioManager.Core.Clients;

namespace PortfolioManager.Core.Statements;

/// <summary>A person who signs the declaration; an empty name leaves a line to write it on.</summary>
public sealed record Signatory(string Name, string Role);

public sealed record Declaration(string Title, string Intro, IReadOnlyList<string> Points, string? Closing, IReadOnlyList<Signatory> Signatories);

public sealed record ReportSection(string? Heading, IReadOnlyList<string> Paragraphs);

public sealed record CompilationReport(string Title, IReadOnlyList<ReportSection> Sections, PracticeDetails? Practice);

/// <summary>The options chosen on the report pack page.</summary>
public sealed record ReportPackOptions(bool IncludeCompilation, DateOnly? SigningDate);

/// <summary>A printable set: cover, contents, statements, notes, declaration and (optionally) the compilation report.</summary>
public sealed record ReportPack(
    FinancialStatements Statements,
    IReadOnlyList<string> CoverLines,
    IReadOnlyList<string> Contents,
    Declaration Declaration,
    CompilationReport? Compilation,
    ReportPackOptions Options,
    IReadOnlyList<string> Warnings)
{
    public string Title => $"Financial statements for the year ended {Statements.YearEndText}";
}

/// <summary>The wording of the report pack for each entity type.</summary>
public static class ReportPackBuilder
{
    public const string NotesTitle = "Notes to the financial statements";

    public static ReportPack Build(FinancialStatements fs, PracticeDetails? practice, ReportPackOptions options)
    {
        var declaration = Declaration(fs.Client, fs.Year);
        var compilation = options.IncludeCompilation ? Compilation(fs, practice) : null;
        var contents = new List<string> { fs.IncomeStatement.Title, fs.FinancialPosition.Title, NotesTitle, declaration.Title };
        if (compilation is not null)
            contents.Add(compilation.Title);

        var warnings = fs.Warnings.ToList();
        if (options.IncludeCompilation && practice is null)
            warnings.Add("Your practice details aren't saved, so the compilation report has no practice name, preparer or tax agent number. Add them on the Practice details page.");
        if (declaration.Signatories.Any(s => s.Name.Length == 0) && fs.Client.EntityType != ClientEntityTypes.Individual)
            warnings.Add("No " + string.Join(" or ", declaration.Signatories.Select(s => s.Role.ToLowerInvariant()).Distinct()) + " is listed in the client details, so the declaration has blank signature lines.");

        return new ReportPack(fs, CoverLines(fs.Client), contents, declaration, compilation, options, warnings);
    }

    /// <summary>The lines under the client's name on the cover: ABN, ACN, trustee.</summary>
    public static IReadOnlyList<string> CoverLines(ClientDetails c)
    {
        var lines = new List<string>();
        if (c.HasTrustee && !string.IsNullOrWhiteSpace(c.TrusteeName))
            lines.Add($"Trustee: {c.TrusteeName}" + (c.CorporateTrustee && !string.IsNullOrEmpty(c.TrusteeAcn) ? $" (ACN {c.TrusteeAcnDisplay})" : ""));
        if (!string.IsNullOrEmpty(c.Abn))
            lines.Add($"ABN {c.AbnDisplay}");
        if (c.EntityType == ClientEntityTypes.Company && !string.IsNullOrEmpty(c.Acn))
            lines.Add($"ACN {c.AcnDisplay}");
        return lines;
    }

    /// <summary>Who signs: the officeholders in the matching role, or blank lines for them.</summary>
    public static IReadOnlyList<Signatory> Signatories(ClientDetails c)
    {
        IReadOnlyList<Signatory> Pick(string label, params string[] roles)
        {
            var people = c.Officeholders.Where(o => roles.Contains(o.Role)).Select(o => new Signatory(o.Name, label)).ToList();
            return people.Count > 0 ? people : [new Signatory("", label), new Signatory("", label)];
        }

        return c.EntityType switch
        {
            ClientEntityTypes.Company => Pick("Director", OfficeholderRoles.Director),
            ClientEntityTypes.Trust or ClientEntityTypes.Smsf when c.CorporateTrustee =>
                Pick("Director of the trustee company", OfficeholderRoles.TrusteeDirector, OfficeholderRoles.Director),
            ClientEntityTypes.Trust or ClientEntityTypes.Smsf => Pick("Trustee", OfficeholderRoles.Trustee),
            ClientEntityTypes.Partnership => Pick("Partner", OfficeholderRoles.Partner),
            _ => [new Signatory(c.LegalName, "")],
        };
    }

    public static Declaration Declaration(ClientDetails c, int year)
    {
        var end = $"30 June {year}";
        var basis = string.IsNullOrWhiteSpace(c.ReportingBasis) ? ClientDetails.DefaultReportingBasis : c.ReportingBasis.Trim();
        var signers = Signatories(c);
        var presentFairly = $"the financial statements and notes present fairly {Possessive(c)} financial position as at {end} and its performance for the year ended on that date in accordance with the accounting policies described in Note 1 to the financial statements";
        string Solvency(string who, string noun) =>
            $"in {who} opinion, there are reasonable grounds to believe that {noun} will be able to pay its debts as and when they become due and payable";

        switch (c.EntityType)
        {
            case ClientEntityTypes.Company:
                return new Declaration("Directors' declaration",
                    $"The directors have determined that the company is not a reporting entity and that this {basis} should be prepared in accordance with the accounting policies described in Note 1 to the financial statements. The directors of {c.LegalName} declare that:",
                    [presentFairly + ";", Solvency("the directors'", "the company") + "."],
                    "This declaration is made in accordance with a resolution of the board of directors.",
                    signers);

            case ClientEntityTypes.Smsf:
            {
                var who = c.CorporateTrustee ? $"The directors of the trustee company, {c.TrusteeName ?? "the trustee"}," : "The trustees";
                return new Declaration(c.CorporateTrustee ? "Trustee's declaration" : "Trustees' declaration",
                    $"{who} have determined that the fund is not a reporting entity and that this {basis} should be prepared in accordance with the accounting policies described in Note 1 to the financial statements. {who} declare that:",
                    [
                        $"the financial statements and notes present fairly the financial position of the fund as at {end} and the results of its operations for the year then ended in accordance with the accounting policies described in Note 1 to the financial statements;",
                        "the financial statements have been prepared in accordance with the requirements of the fund's trust deed; and",
                        $"the operation of the fund has been carried out in accordance with its trust deed and in compliance with the requirements of the Superannuation Industry (Supervision) Act 1993 and Regulations during the year ended {end}.",
                    ],
                    null, signers);
            }

            case ClientEntityTypes.Trust:
            {
                var who = c.CorporateTrustee ? $"The directors of the trustee company, {c.TrusteeName ?? "the trustee"}," : "The trustee";
                var verb = c.CorporateTrustee ? "have" : "has";
                var declares = c.CorporateTrustee ? "declare" : "declares";
                return new Declaration("Trustee's declaration",
                    $"{who} {verb} determined that the trust is not a reporting entity and that this {basis} should be prepared in accordance with the accounting policies described in Note 1 to the financial statements. {who} {declares} that:",
                    [presentFairly + ";", Solvency(c.CorporateTrustee ? "the directors'" : "the trustee's", "the trust") + "."],
                    null, signers);
            }

            case ClientEntityTypes.Partnership:
                return new Declaration("Partners' declaration",
                    $"The partners have determined that the partnership is not a reporting entity and that this {basis} should be prepared in accordance with the accounting policies described in Note 1 to the financial statements. The partners of {c.LegalName} declare that:",
                    [presentFairly + ";", Solvency("the partners'", "the partnership") + "."],
                    null, signers);

            default:
                return new Declaration("Declaration",
                    $"I have determined that this {basis} should be prepared in accordance with the accounting policies described in Note 1 to the financial statements. I declare that:",
                    [
                        $"the financial statements and notes present fairly my financial position as at {end} and my performance for the year ended on that date in accordance with the accounting policies described in Note 1 to the financial statements; and",
                        "I have given my accountant all the information needed to prepare them, and it is complete and accurate.",
                    ],
                    null, signers);
        }
    }

    /// <summary>The compilation report in the form of APES 315 Compilation of Financial Information.</summary>
    public static CompilationReport Compilation(FinancialStatements fs, PracticeDetails? practice)
    {
        var c = fs.Client;
        var basis = string.IsNullOrWhiteSpace(c.ReportingBasis) ? ClientDetails.DefaultReportingBasis : c.ReportingBasis.Trim();
        var statementsWord = basis.EndsWith("report", StringComparison.OrdinalIgnoreCase) ? basis[..^"report".Length].TrimEnd() + " statements" : "financial statements";
        var responsible = StatementWording.Responsible(c);
        var Responsible = char.ToUpperInvariant(responsible[0]) + responsible[1..];
        var isAre = StatementWording.ResponsibleIsSingular(c) ? "is" : "are";
        var of = c.EntityType == ClientEntityTypes.Individual ? "" : $" of {c.LegalName}";

        return new CompilationReport($"Compilation report to {c.LegalName}",
        [
            new(null,
            [
                $"We have compiled the accompanying {statementsWord} of {c.LegalName}, which comprise the {fs.FinancialPosition.Title.ToLowerInvariant()} as at {fs.YearEndText}, the {fs.IncomeStatement.Title.ToLowerInvariant()} for the year then ended, a summary of significant accounting policies and other explanatory notes. These have been prepared in accordance with the financial reporting framework described in Note 1 to the financial statements.",
            ]),
            new($"The responsibility of {responsible}",
            [
                $"{Responsible}{of} {isAre} solely responsible for the information contained in the {statementsWord}, the reliability, accuracy and completeness of the information, and for the determination that the financial reporting framework used is appropriate to meet their needs and for the purpose for which the financial statements were prepared.",
            ]),
            new("Our responsibility",
            [
                $"On the basis of information provided by {responsible}, we have compiled the accompanying {statementsWord} in accordance with the financial reporting framework described in Note 1 to the financial statements and APES 315 Compilation of Financial Information.",
                "We have applied our expertise in accounting and financial reporting to compile these financial statements in accordance with the financial reporting framework described in Note 1 to the financial statements. We have complied with the relevant ethical requirements of APES 110 Code of Ethics for Professional Accountants (including Independence Standards).",
            ]),
            new("Assurance disclaimer",
            [
                "Since a compilation engagement is not an assurance engagement, we are not required to verify the reliability, accuracy or completeness of the information provided to us to compile these financial statements. Accordingly, we do not express an audit opinion or a review conclusion on these financial statements.",
                $"The {statementsWord} were compiled exclusively for the benefit of {responsible}, who {isAre} responsible for the reliability, accuracy and completeness of the information used to compile them. We do not accept responsibility for the contents of the {statementsWord}.",
            ]),
        ], practice);
    }

    private static string Possessive(ClientDetails c) => c.EntityType switch
    {
        ClientEntityTypes.Company => "the company's",
        ClientEntityTypes.Trust => "the trust's",
        ClientEntityTypes.Smsf => "the fund's",
        ClientEntityTypes.Partnership => "the partnership's",
        _ => "the",
    };
}
