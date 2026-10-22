using PortfolioManager.Core.Clients;
using PortfolioManager.Core.Statements;

namespace PortfolioManager.Tests;

public class ReportPackTests
{
    private static ClientDetails Client(string type, string? trusteeType = null, params Officeholder[] people) => new(
        1, "Test", type, true, "Smith Holdings", type, "51824753556", type == ClientEntityTypes.Company ? "000000019" : null,
        trusteeType is null ? null : "Smith Nominees Pty Ltd", trusteeType, trusteeType == TrusteeTypes.Corporate ? "000000019" : null,
        new PostalAddress(null, null, null, null, null), ClientDetails.DefaultReportingBasis, null, people);

    private static FinancialStatements Statements(ClientDetails c)
    {
        var s = new LedgerSnapshot(new DateOnly(2026, 6, 30), [], 0, 0);
        return FinancialStatementsBuilder.Build(c, 2026, s, s, s);
    }

    private static readonly PracticeDetails Practice = new("Darcy Tax", "Tony Darcy", "12345678", "CPA",
        new PostalAddress("2 Pitt St", null, "Sydney", "NSW", "2000"), null, null);

    [Fact]
    public void Company_directors_sign_and_the_cover_shows_abn_and_acn()
    {
        var c = Client(ClientEntityTypes.Company, null,
            new Officeholder("Jane Smith", OfficeholderRoles.Director), new Officeholder("Ann Lee", OfficeholderRoles.Secretary));
        var pack = ReportPackBuilder.Build(Statements(c), Practice, new ReportPackOptions(true, null));

        Assert.Equal("Directors' declaration", pack.Declaration.Title);
        Assert.Equal([new Signatory("Jane Smith", "Director")], pack.Declaration.Signatories);
        Assert.Contains("resolution of the board", pack.Declaration.Closing);
        Assert.Equal(["ABN 51 824 753 556", "ACN 000 000 019"], pack.CoverLines);
        Assert.Equal("Financial statements for the year ended 30 June 2026", pack.Title);
        Assert.Equal(5, pack.Contents.Count);
        Assert.Equal("Compilation report to Smith Holdings", pack.Contents[^1]);
    }

    [Fact]
    public void A_trust_with_a_corporate_trustee_is_signed_by_its_directors()
    {
        var c = Client(ClientEntityTypes.Trust, TrusteeTypes.Corporate, new Officeholder("Jane Smith", OfficeholderRoles.TrusteeDirector));
        var d = ReportPackBuilder.Declaration(c, 2026);

        Assert.Equal("Trustee's declaration", d.Title);
        Assert.StartsWith("The directors of the trustee company, Smith Nominees Pty Ltd, have determined", d.Intro);
        Assert.Equal("Director of the trustee company", Assert.Single(d.Signatories).Role);
        Assert.Contains("Trustee: Smith Nominees Pty Ltd (ACN 000 000 019)", ReportPackBuilder.CoverLines(c));
    }

    [Fact]
    public void A_trust_with_an_individual_trustee_uses_the_singular()
    {
        var d = ReportPackBuilder.Declaration(Client(ClientEntityTypes.Trust, TrusteeTypes.Individual, new Officeholder("Bob", OfficeholderRoles.Trustee)), 2026);
        Assert.StartsWith("The trustee has determined", d.Intro);
        Assert.Contains("The trustee declares that:", d.Intro);
        Assert.Contains("in the trustee's opinion", d.Points[1]);
    }

    [Fact]
    public void Smsf_trustees_declare_compliance_with_the_sis_act()
    {
        var c = Client(ClientEntityTypes.Smsf, TrusteeTypes.Individual,
            new Officeholder("Jane Smith", OfficeholderRoles.Trustee), new Officeholder("John Smith", OfficeholderRoles.Trustee),
            new Officeholder("Jane Smith", OfficeholderRoles.Member));
        var d = ReportPackBuilder.Declaration(c, 2026);

        Assert.Equal("Trustees' declaration", d.Title);
        Assert.Equal(3, d.Points.Count);
        Assert.Contains("Superannuation Industry (Supervision) Act 1993", d.Points[2]);
        Assert.Equal(["Jane Smith", "John Smith"], d.Signatories.Select(s => s.Name));
    }

    [Fact]
    public void Partners_sign_a_partnership_declaration()
    {
        var d = ReportPackBuilder.Declaration(Client(ClientEntityTypes.Partnership, null,
            new Officeholder("A", OfficeholderRoles.Partner), new Officeholder("B", OfficeholderRoles.Partner)), 2026);
        Assert.Equal("Partners' declaration", d.Title);
        Assert.Equal(2, d.Signatories.Count);
        Assert.All(d.Signatories, s => Assert.Equal("Partner", s.Role));
    }

    [Fact]
    public void An_individual_signs_their_own_declaration()
    {
        var d = ReportPackBuilder.Declaration(Client(ClientEntityTypes.Individual), 2026);
        Assert.Equal("Declaration", d.Title);
        Assert.Contains("my financial position as at 30 June 2026", d.Points[0]);
        Assert.Equal("Smith Holdings", Assert.Single(d.Signatories).Name);
    }

    [Fact]
    public void Missing_signatories_leave_blank_lines_and_a_warning()
    {
        var pack = ReportPackBuilder.Build(Statements(Client(ClientEntityTypes.Company)), Practice, new ReportPackOptions(false, null));
        Assert.Equal(2, pack.Declaration.Signatories.Count(s => s.Name.Length == 0));
        Assert.Contains(pack.Warnings, w => w.Contains("No director"));
        Assert.Null(pack.Compilation);
        Assert.Equal(4, pack.Contents.Count);
    }

    [Fact]
    public void The_compilation_report_follows_apes_315_and_needs_practice_details()
    {
        var c = Client(ClientEntityTypes.Company, null, new Officeholder("Jane Smith", OfficeholderRoles.Director));
        var fs = Statements(c);
        var report = ReportPackBuilder.Compilation(fs, Practice);

        Assert.Equal(["The responsibility of the directors", "Our responsibility", "Assurance disclaimer"],
            report.Sections.Where(s => s.Heading is not null).Select(s => s.Heading));
        var text = string.Join(" ", report.Sections.SelectMany(s => s.Paragraphs));
        Assert.Contains("APES 315 Compilation of Financial Information", text);
        Assert.Contains("APES 110 Code of Ethics", text);
        Assert.Contains("special purpose financial statements of Smith Holdings", text);
        Assert.Contains("we do not express an audit opinion or a review conclusion", text);
        Assert.Contains("The directors of Smith Holdings are solely responsible", text);
        Assert.Same(Practice, report.Practice);

        var pack = ReportPackBuilder.Build(fs, null, new ReportPackOptions(true, new DateOnly(2026, 10, 15)));
        Assert.Contains(pack.Warnings, w => w.Contains("practice details aren't saved"));
    }

    [Fact]
    public void An_individual_trustee_is_singular_in_the_compilation_report()
    {
        var c = Client(ClientEntityTypes.Trust, TrusteeTypes.Individual);
        var text = string.Join(" ", ReportPackBuilder.Compilation(Statements(c), Practice).Sections.SelectMany(s => s.Paragraphs));
        Assert.Contains("The trustee of Smith Holdings is solely responsible", text);
    }
}
