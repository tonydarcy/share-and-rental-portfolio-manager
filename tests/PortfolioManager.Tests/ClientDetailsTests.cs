using PortfolioManager.Core.Clients;

namespace PortfolioManager.Tests;

public class BusinessNumberTests
{
    [Theory]
    [InlineData("51 824 753 556")] // the ATO's worked example
    [InlineData("51824753556")]
    [InlineData("53 004 085 616")]
    [InlineData("33 102 417 032")]
    public void Valid_abns_pass(string abn) => Assert.True(BusinessNumbers.IsValidAbn(abn));

    [Theory]
    [InlineData("51 824 753 557")]
    [InlineData("12 345 678 901")]
    [InlineData("5182475355")]
    [InlineData("518247535566")]
    [InlineData("51-824-753-556")]
    [InlineData("5182475355A")]
    [InlineData("")]
    [InlineData(null)]
    public void Invalid_abns_fail(string? abn) => Assert.False(BusinessNumbers.IsValidAbn(abn));

    [Theory]
    [InlineData("000 000 019")] // ASIC's examples
    [InlineData("000 250 000")]
    [InlineData("001 749 999")]
    [InlineData("004 085 616")]
    [InlineData("010 499 966")]
    [InlineData("010749961")]
    public void Valid_acns_pass(string acn) => Assert.True(BusinessNumbers.IsValidAcn(acn));

    [Theory]
    [InlineData("000 000 018")]
    [InlineData("004 085 617")]
    [InlineData("00408561")]
    [InlineData("0040856166")]
    [InlineData("ABC085616")]
    [InlineData(null)]
    public void Invalid_acns_fail(string? acn) => Assert.False(BusinessNumbers.IsValidAcn(acn));

    [Fact]
    public void A_company_abn_ends_with_its_acn()
    {
        Assert.True(BusinessNumbers.AbnMatchesAcn("53 004 085 616", "004 085 616"));
        Assert.False(BusinessNumbers.AbnMatchesAcn("51 824 753 556", "004 085 616"));
    }

    [Fact]
    public void Numbers_are_shown_in_groups()
    {
        Assert.Equal("51 824 753 556", BusinessNumbers.FormatAbn("51824753556"));
        Assert.Equal("004 085 616", BusinessNumbers.FormatAcn("004085616"));
        Assert.Equal("", BusinessNumbers.FormatAbn(null));
        Assert.Equal("12", BusinessNumbers.FormatAbn("12"));
    }
}

public class ClientDetailsInputTests
{
    private static ClientDetailsInput Company() => new()
    {
        LegalName = "Smith Investments Pty Ltd",
        EntityType = ClientEntityTypes.Company,
        Abn = "53 004 085 616",
        Acn = "004 085 616",
        Officeholders = [new() { Name = "Jane Smith", Role = OfficeholderRoles.Director }, new()],
    };

    [Fact]
    public void A_complete_company_passes() => Assert.Empty(Company().Validate());

    [Fact]
    public void A_bad_abn_is_rejected()
    {
        var input = Company();
        input.Abn = "53 004 085 617";
        Assert.Contains(input.Validate(), e => e.Contains("ABN"));
    }

    [Fact]
    public void A_bad_acn_is_rejected()
    {
        var input = Company();
        input.Acn = "004 085 617";
        Assert.Contains(input.Validate(), e => e.Contains("ACN"));
    }

    [Fact]
    public void A_company_abn_must_match_its_acn()
    {
        var input = Company();
        input.Abn = "51 824 753 556";
        Assert.Contains(input.Validate(), e => e.Contains("don't match"));
    }

    [Fact]
    public void The_acn_is_ignored_for_a_trust()
    {
        var input = Company();
        input.EntityType = ClientEntityTypes.Trust;
        input.Acn = "not a number";
        Assert.Empty(input.Validate());
        Assert.Null(input.CleanAcn);
    }

    [Fact]
    public void A_corporate_trustee_acn_is_checked_and_kept()
    {
        var input = new ClientDetailsInput
        {
            LegalName = "Smith Super Fund",
            EntityType = ClientEntityTypes.Smsf,
            TrusteeName = "Smith Super Pty Ltd",
            TrusteeType = TrusteeTypes.Corporate,
            TrusteeAcn = "000 000 018",
        };
        Assert.Contains(input.Validate(), e => e.Contains("trustee company's ACN"));
        input.TrusteeAcn = "000 000 019";
        Assert.Empty(input.Validate());
        Assert.Equal("000000019", input.CleanTrusteeAcn);
    }

    [Fact]
    public void Trustee_fields_are_dropped_for_an_individual()
    {
        var input = new ClientDetailsInput { LegalName = "Jane Smith", TrusteeName = "x", TrusteeType = "Corporate", TrusteeAcn = "1" };
        Assert.Empty(input.Validate());
        Assert.Null(input.CleanTrusteeName);
        Assert.Null(input.CleanTrusteeType);
        Assert.Null(input.CleanTrusteeAcn);
    }

    [Theory]
    [InlineData("", "Individual")]
    [InlineData("Jane", "Sole trader")]
    public void A_name_and_known_type_are_needed(string name, string type) =>
        Assert.NotEmpty(new ClientDetailsInput { LegalName = name, EntityType = type }.Validate());

    [Theory]
    [InlineData("NSW", "2000", true)]
    [InlineData("", "", true)]
    [InlineData("NZ", "2000", false)]
    [InlineData("VIC", "300", false)]
    [InlineData("VIC", "30OO", false)]
    public void The_address_is_checked(string state, string postcode, bool ok) =>
        Assert.Equal(ok, new ClientDetailsInput { LegalName = "Jane", State = state, Postcode = postcode }.Validate().Count == 0);

    [Fact]
    public void Officeholders_need_a_known_role_and_blank_rows_are_skipped()
    {
        var input = Company();
        Assert.Single(input.UsedOfficeholders);
        input.Officeholders.Add(new() { Name = "Bob", Role = "Chief" });
        Assert.Contains(input.Validate(), e => e.Contains("role"));
    }

    [Fact]
    public void The_reporting_basis_defaults()
    {
        Assert.Equal(ClientDetails.DefaultReportingBasis, new ClientDetailsInput { ReportingBasis = "  " }.CleanReportingBasis);
        Assert.Equal("general purpose", new ClientDetailsInput { ReportingBasis = " general purpose " }.CleanReportingBasis);
    }

    [Theory]
    [InlineData("Superfund", ClientEntityTypes.Smsf)]
    [InlineData("Company", ClientEntityTypes.Company)]
    [InlineData("Individual", ClientEntityTypes.Individual)]
    [InlineData("Other", ClientEntityTypes.Individual)]
    [InlineData(null, ClientEntityTypes.Individual)]
    public void Unsaved_details_guess_the_type_from_the_portfolio(string? portfolioType, string expected) =>
        Assert.Equal(expected, ClientEntityTypes.FromPortfolioType(portfolioType));
}

public class PracticeDetailsInputTests
{
    [Fact]
    public void A_complete_practice_passes() => Assert.Empty(new PracticeDetailsInput
    {
        PracticeName = "Darcy Tax", PreparerName = "Tony Darcy", TaxAgentNumber = "1234 5678", Email = "tony@example.com",
    }.Validate());

    [Theory]
    [InlineData("", "Tony", null)]
    [InlineData("Darcy Tax", "", null)]
    [InlineData("Darcy Tax", "Tony", "1234567")]
    [InlineData("Darcy Tax", "Tony", "12345678X")]
    public void Bad_practice_details_are_rejected(string practice, string preparer, string? tan) =>
        Assert.NotEmpty(new PracticeDetailsInput { PracticeName = practice, PreparerName = preparer, TaxAgentNumber = tan }.Validate());

    [Fact]
    public void The_tax_agent_number_is_saved_without_spaces() =>
        Assert.Equal("12345678", new PracticeDetailsInput { TaxAgentNumber = " 1234 5678 " }.CleanTaxAgentNumber);
}

public class ClientMapTests
{
    private static Dictionary<string, object?> Row(bool saved, string? type = "Trust") => new()
    {
        ["iUserId"] = 7,
        ["strEntityName"] = "Smith fund ",
        ["strPortfolioEntityType"] = "Superfund",
        ["bSaved"] = saved,
        ["strLegalName"] = saved ? "Smith Family Trust" : null,
        ["strEntityType"] = saved ? type : null,
        ["strABN"] = saved ? "51824753556" : null,
        ["strACN"] = DBNull.Value,
        ["strTrusteeName"] = saved ? "Smith Nominees Pty Ltd" : null,
        ["strTrusteeType"] = saved ? "Corporate" : null,
        ["strTrusteeACN"] = saved ? "000000019" : null,
        ["strAddressLine1"] = saved ? "1 George St" : null,
        ["strAddressLine2"] = null,
        ["strSuburb"] = saved ? "Sydney" : null,
        ["strState"] = saved ? "NSW" : null,
        ["strPostcode"] = saved ? "2000" : null,
        ["strReportingBasis"] = saved ? "special purpose financial report" : null,
        ["dtmUpdated"] = saved ? new DateTime(2026, 10, 15) : null,
    };

    [Fact]
    public void Saved_details_are_read()
    {
        var people = new[] { new Dictionary<string, object?> { ["strName"] = "Jane Smith", ["strRole"] = "Director of trustee company" } };
        var d = ClientService.Map(Row(true), people);
        Assert.True(d.Saved);
        Assert.Equal("Smith Family Trust", d.LegalName);
        Assert.Equal(ClientEntityTypes.Trust, d.EntityType);
        Assert.Equal("51 824 753 556", d.AbnDisplay);
        Assert.True(d.CorporateTrustee);
        Assert.Equal("1 George St, Sydney NSW 2000", d.Address.OneLine);
        Assert.Equal("Jane Smith", Assert.Single(d.Officeholders).Name);
    }

    [Fact]
    public void Unsaved_details_come_from_the_portfolio()
    {
        var d = ClientService.Map(Row(false), []);
        Assert.False(d.Saved);
        Assert.Equal("Smith fund", d.LegalName);
        Assert.Equal(ClientEntityTypes.Smsf, d.EntityType);
        Assert.Equal(ClientDetails.DefaultReportingBasis, d.ReportingBasis);
        Assert.Empty(d.Address.Lines);
    }

    [Fact]
    public void The_client_list_keeps_only_accessible_portfolios()
    {
        var other = Row(false);
        other["iUserId"] = 8;
        var list = ClientService.MapList([Row(true), other], new HashSet<int> { 7 });
        Assert.Equal(7, Assert.Single(list).PortfolioId);
    }
}
