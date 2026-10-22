using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Clients;
using PortfolioManager.Core.Practice;

namespace PortfolioManager.Tests;

public class PracticeTests
{
    [Theory]
    [InlineData("statement.pdf", "application/pdf")]
    [InlineData("Photo.JPG", "image/jpeg")]
    [InlineData("page.html", null)]
    [InlineData("script.exe", null)]
    public void Only_known_file_types_are_served(string name, string? type) => Assert.Equal(type, AttachmentRules.ContentTypeFor(name));

    [Fact]
    public void Attachments_are_checked_for_type_and_size()
    {
        Assert.Null(AttachmentRules.Validate("a.pdf", 1000));
        Assert.NotNull(AttachmentRules.Validate("a.pdf", 0));
        Assert.NotNull(AttachmentRules.Validate("a.pdf", 11 * 1024 * 1024));
        Assert.NotNull(AttachmentRules.Validate("a.svg", 1000));
    }

    [Fact]
    public void File_names_lose_folders()
    {
        var clean = AttachmentRules.CleanName(@"C:\Users\x\..\contract note.pdf");
        Assert.DoesNotContain("\\", clean);
        Assert.EndsWith("contract note.pdf", clean);
    }

    [Fact]
    public void Changes_to_records_in_a_once_locked_period_are_marked()
    {
        var history = new List<LockChange> { new(new DateOnly(2025, 6, 30), new DateTime(2025, 9, 1), "tony"), new(null, new DateTime(2025, 10, 1), "tony") };
        var entries = new[]
        {
            new AuditEntry(1, new DateTime(2025, 10, 2), "tony", null, "Journal", 5, "Update", new DateOnly(2025, 3, 1), null),
            new AuditEntry(2, new DateTime(2025, 10, 2), "tony", null, "Journal", 6, "Update", new DateOnly(2025, 8, 1), null),
            new AuditEntry(3, new DateTime(2025, 8, 1), "tony", null, "Journal", 7, "Update", new DateOnly(2025, 3, 1), null),
        };
        var marked = AuditRules.MarkAfterLock(entries, history);
        Assert.True(marked[0].AfterLock);
        Assert.False(marked[1].AfterLock);
        Assert.False(marked[2].AfterLock);
    }

    [Fact]
    public void Audit_entries_without_a_login_say_so() =>
        Assert.Equal("No login (SSMS)", new AuditEntry(1, DateTime.Now, null, "SSMS", "Trade", 1, "Insert", null, null).Who);

    [Fact]
    public void Locked_years_show_as_locked()
    {
        Assert.Equal(YearStatuses.Locked, YearStatuses.Effective(YearStatuses.Review, true));
        Assert.Equal(YearStatuses.Review, YearStatuses.Effective(YearStatuses.Review, false));
        Assert.Equal(YearStatuses.NotStarted, YearStatuses.Effective("Rubbish", false));
    }

    [Fact]
    public void Checklist_has_each_entity_types_own_steps_before_signing()
    {
        var smsf = YearEndChecklist.For(ClientEntityTypes.Smsf);
        Assert.Contains(smsf, i => i.Key == "tbar");
        Assert.Equal("lock", smsf[^1].Key);
        Assert.True(smsf.ToList().FindIndex(i => i.Key == "audit") < smsf.ToList().FindIndex(i => i.Key == "signed"));
        Assert.DoesNotContain(YearEndChecklist.For(ClientEntityTypes.Individual), i => i.Key == "tbar");
        Assert.Equal(smsf.Count, smsf.Select(i => i.Key).Distinct().Count());
    }

    [Fact]
    public void Onboarding_details_round_trip_and_become_client_details()
    {
        var d = new OnboardingDetails
        {
            LegalName = "Smith Family Trust", TrusteeName = "Smith Holdings Pty Ltd", State = "nsw", People = "Jane Smith, Director\nJohn Smith, Director",
        };
        var back = OnboardingDetails.FromXml(d.ToXml());
        Assert.Equal("Smith Family Trust", back.LegalName);
        Assert.Equal(d.People, back.People);
        var c = back.ToClientDetails(ClientEntityTypes.Trust);
        Assert.Equal("NSW", c.State);
        Assert.Equal(TrusteeTypes.Corporate, c.TrusteeType);
        Assert.Equal(2, c.Officeholders.Count);
        Assert.Equal("Director", c.Officeholders[1].Role);
    }

    [Fact]
    public void Onboarding_asks_a_company_for_its_acn()
    {
        Assert.Contains(new OnboardingDetails { LegalName = "X Pty Ltd" }.Validate(ClientEntityTypes.Company), e => e.Contains("ACN"));
        Assert.Contains(new OnboardingDetails().Validate(ClientEntityTypes.Individual), e => e.Contains("legal name"));
    }

    [Fact]
    public void Onboarding_links_are_long_and_random()
    {
        var a = OnboardingService.NewToken();
        Assert.Equal(64, a.Length);
        Assert.NotEqual(a, OnboardingService.NewToken());
    }
}
