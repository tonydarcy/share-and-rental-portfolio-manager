using PortfolioManager.Web.Services;

namespace PortfolioManager.Tests;

public class LegalTests
{
    [Fact]
    public void Legal_pages_are_on_by_default_and_ask_everyone_to_agree()
    {
        var legal = new LegalSettings();
        Assert.True(legal.Enabled);
        Assert.True(legal.AsksForAcceptance);
        Assert.Equal(LegalSettings.BuiltInVersion, legal.Version);
        Assert.Equal(LegalSettings.DefaultSourceCodeUrl, legal.SourceCodeUrl);
    }

    [Fact]
    public void A_host_running_its_own_copy_can_turn_them_off()
    {
        Assert.False(new LegalSettings { Enabled = false }.AsksForAcceptance);
        Assert.False(new LegalSettings { RequireAcceptance = false }.AsksForAcceptance);
    }

    [Fact]
    public void A_new_terms_version_asks_everyone_again() =>
        Assert.Equal("2027-01-01", new LegalSettings { TermsVersion = " 2027-01-01 " }.Version);

    [Fact]
    public void The_operator_and_contact_fall_back_to_the_site_settings()
    {
        var site = new SiteBranding { Name = "My Portfolios", SupportEmail = "help@example.com" };
        Assert.Equal("My Portfolios", new LegalSettings().Operator(site));
        Assert.Equal("help@example.com", new LegalSettings().Contact(site));
        var legal = new LegalSettings { OperatorName = "Example Pty Ltd", ContactEmail = "privacy@example.com" };
        Assert.Equal("Example Pty Ltd", legal.Operator(site));
        Assert.Equal("privacy@example.com", legal.Contact(site));
    }

    [Theory]
    [InlineData("/TaxWork/Rental", true)]
    [InlineData("/TaxWork/PaperReturn", true)]
    [InlineData("/SalesTax/Index", true)]
    [InlineData("/Reports/CapitalGains", true)]
    [InlineData("/Reports/Consolidated", true)]
    [InlineData("/Reports/Performance", false)]
    [InlineData("/Holdings", false)]
    [InlineData(null, false)]
    public void Tax_and_lodgement_pages_carry_the_check_these_figures_note(string? page, bool shown) =>
        Assert.Equal(shown, LegalSettings.ShowsFiguresNote(page));

    [Fact]
    public void The_assistant_page_says_answers_can_be_wrong()
    {
        Assert.True(LegalSettings.ShowsAssistantNote("/Assistant/Index"));
        Assert.False(LegalSettings.ShowsAssistantNote("/Index"));
        Assert.Contains("Anthropic", LegalSettings.AssistantNote);
    }

    [Theory]
    [InlineData("/Legal/Accept", true)]
    [InlineData("/Legal/Terms", true)]
    [InlineData("/Account/Logout", true)]
    [InlineData("/Error", true)]
    [InlineData("/Index", false)]
    [InlineData("/Trades/Index", false)]
    public void Someone_who_hasnt_agreed_can_still_read_the_terms_or_log_out(string page, bool open) =>
        Assert.Equal(open, LegalSettings.OpenBeforeAccepting(page));
}
