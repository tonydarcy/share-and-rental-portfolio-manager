using PortfolioManager.Core.Alerts;

namespace PortfolioManager.Tests;

public class AlertTests
{
    [Theory]
    [InlineData("Above", 10, 10, true)]
    [InlineData("Above", 10, 9.99, false)]
    [InlineData("Below", 10, 10, true)]
    [InlineData("Below", 10, 10.01, false)]
    [InlineData("below", 10, 5, true)]
    public void Price_target_is_reached_at_or_past_it(string direction, decimal target, decimal close, bool reached) =>
        Assert.Equal(reached, AlertKinds.Reached(direction, target, close));

    [Fact]
    public void One_alert_puts_its_text_in_the_subject()
    {
        var d = new AlertDigest(1, 2, "a@b.com", "Smith Super", [new AlertItem(AlertKinds.Sync, "The Navexa sync didn't finish: bad key")]);
        var mail = AlertKinds.Email(d, "Share Portfolio Manager", "https://app.example.com/");
        Assert.Equal("a@b.com", mail.To);
        Assert.Equal("Smith Super: The Navexa sync didn't finish: bad key", mail.Subject);
        Assert.Contains("bad key", mail.Text);
        Assert.Contains("https://app.example.com/", mail.Text + mail.Html);
    }

    [Fact]
    public void Several_alerts_are_counted_in_the_subject()
    {
        var d = new AlertDigest(1, 2, "a@b.com", "Smith Super",
            [new AlertItem(AlertKinds.Price, "CBA closed at 150", 7, 150m), new AlertItem(AlertKinds.Unallocated, "3 imported bank lines are waiting")]);
        var mail = AlertKinds.Email(d, "SPM", "https://x/");
        Assert.Equal("Smith Super: 2 alerts", mail.Subject);
        Assert.True(d.HasUnallocated);
        Assert.Contains("CBA closed at 150", mail.Text);
    }

    [Fact]
    public void Long_subject_is_cut()
    {
        var d = new AlertDigest(1, 2, "a@b.com", "P", [new AlertItem(AlertKinds.Sync, new string('x', 300))]);
        Assert.Equal(150, AlertKinds.Email(d, "SPM", "https://x/").Subject.Length);
    }

    [Fact]
    public void New_settings_default_to_everything_but_bank_lines() =>
        Assert.Equal(new AlertSettings(true, true, true, false), AlertSettings.Default);
}
