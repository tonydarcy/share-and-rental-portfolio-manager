using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using PortfolioManager.Core.Accounts;
using PortfolioManager.Core.Email;
using PortfolioManager.Core.Invoicing;
using PortfolioManager.Core.Security;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Tests;

public class AccountSecurityTests
{
    // RFC 6238 appendix B: the ASCII secret "12345678901234567890", 8-digit codes cut to the 6 apps show
    private static readonly string RfcSecret = TwoFactor.ToBase32(Encoding.ASCII.GetBytes("12345678901234567890"));

    [Theory]
    [InlineData(59, "287082")]
    [InlineData(1111111109, "081804")]
    [InlineData(1234567890, "005924")]
    [InlineData(2000000000, "279037")]
    public void Authenticator_codes_match_the_standard(long unixSeconds, string expected) =>
        Assert.Equal(expected, TwoFactor.Code(RfcSecret, DateTimeOffset.FromUnixTimeSeconds(unixSeconds)));

    [Fact]
    public void Codes_are_accepted_30_seconds_either_side_but_not_further()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var secret = TwoFactor.NewSecret();
        Assert.True(TwoFactor.VerifyCode(secret, TwoFactor.Code(secret, now), now));
        Assert.True(TwoFactor.VerifyCode(secret, TwoFactor.Code(secret, now.AddSeconds(-30)), now));
        Assert.True(TwoFactor.VerifyCode(secret, TwoFactor.Code(secret, now.AddSeconds(30)), now));
        Assert.False(TwoFactor.VerifyCode(secret, TwoFactor.Code(secret, now.AddMinutes(-2)), now));
        var spaced = TwoFactor.Code(secret, now).Insert(3, " ");
        Assert.True(TwoFactor.VerifyCode(secret, spaced, now));
        Assert.False(TwoFactor.VerifyCode(secret, "abcdef", now));
        Assert.False(TwoFactor.VerifyCode(null, "123456", now));
    }

    [Fact]
    public void Base32_round_trips()
    {
        var bytes = Enumerable.Range(0, 20).Select(i => (byte)(i * 13)).ToArray();
        Assert.Equal(bytes, TwoFactor.FromBase32(TwoFactor.ToBase32(bytes)));
        Assert.Equal(32, TwoFactor.NewSecret().Length);
    }

    [Fact]
    public void Authenticator_address_names_the_site_and_login()
    {
        var uri = TwoFactor.AuthenticatorUri("Smith & Co", "a@b.com", "ABC");
        Assert.Equal("otpauth://totp/Smith%20%26%20Co:a%40b.com?secret=ABC&issuer=Smith%20%26%20Co&digits=6&period=30", uri);
    }

    [Fact]
    public void Each_recovery_code_works_once()
    {
        var (codes, stored) = TwoFactor.NewRecoveryCodes();
        Assert.Equal(TwoFactor.RecoveryCodeCount, codes.Count);
        Assert.DoesNotContain(codes[0], stored); // only hashes are kept

        var left = TwoFactor.UseRecoveryCode(stored, codes[0].ToUpperInvariant());
        Assert.NotNull(left);
        Assert.Equal(TwoFactor.RecoveryCodeCount - 1, TwoFactor.RecoveryCodesLeft(left));
        Assert.Null(TwoFactor.UseRecoveryCode(left, codes[0]));
        // Typed without the dash
        Assert.NotNull(TwoFactor.UseRecoveryCode(left, codes[1].Replace("-", "")));
        Assert.Null(TwoFactor.UseRecoveryCode(stored, "nope"));
        Assert.Null(TwoFactor.UseRecoveryCode(null, codes[0]));
    }

    private static AccountTokens Tokens() => new(new EphemeralDataProtectionProvider());

    [Fact]
    public void Reset_tokens_carry_the_login_and_password_fingerprint_for_their_purpose_only()
    {
        var tokens = Tokens();
        var user = new SignedInUser(42, "a@b.com", PasswordStamp: AccountService.StampOf("PBKDF2$old"));
        var token = tokens.PasswordReset(user);

        Assert.Equal((42, user.PasswordStamp), tokens.Read(AccountTokens.PasswordResetPurpose, token));
        Assert.Null(tokens.Read(AccountTokens.ConfirmEmailPurpose, token));
        Assert.Null(tokens.Read(AccountTokens.PasswordResetPurpose, token[..^2] + "xx"));
        Assert.Null(tokens.Read(AccountTokens.PasswordResetPurpose, null));
        // A new password changes the fingerprint, so the old link no longer matches
        Assert.NotEqual(user.PasswordStamp, AccountService.StampOf("PBKDF2$new"));
    }

    [Fact]
    public void Tokens_expire()
    {
        var tokens = Tokens();
        var token = tokens.Make(AccountTokens.ConfirmEmailPurpose, 1, "a@b.com", TimeSpan.FromSeconds(-1));
        Assert.Null(tokens.Read(AccountTokens.ConfirmEmailPurpose, token));
    }

    [Fact]
    public void Two_factor_secrets_are_stored_protected()
    {
        var tokens = Tokens();
        var stored = tokens.ProtectSecret("JBSWY3DPEHPK3PXP");
        Assert.NotEqual("JBSWY3DPEHPK3PXP", stored);
        Assert.Equal("JBSWY3DPEHPK3PXP", tokens.UnprotectSecret(stored));
        Assert.Null(tokens.UnprotectSecret("garbage"));
        Assert.Null(Tokens().UnprotectSecret(stored)); // other keys can't read it
    }

    [Fact]
    public void New_passwords_are_checked()
    {
        Assert.Empty(AccountService.ValidatePassword("long enough pw", "long enough pw"));
        Assert.Contains(AccountService.ValidatePassword("short", "short"), e => e.Contains("at least"));
        Assert.Contains(AccountService.ValidatePassword("long enough pw", "different one"), e => e.Contains("don't match"));
    }

    [Fact]
    public void Account_emails_encode_what_people_typed()
    {
        var m = EmailTemplates.Invitation("new@b.com", "Site", "owner@b.com", "<Smith> Trust", "an editor", "https://x/Account/Register?email=new%40b.com", false);
        Assert.Equal("owner@b.com shared \"<Smith> Trust\" with you", m.Subject);
        Assert.Contains("&lt;Smith&gt; Trust", m.Html);
        Assert.DoesNotContain("<Smith>", m.Html);
        Assert.Contains("Sign up: https://x/Account/Register?email=new%40b.com", m.Text);
        Assert.Equal("owner@b.com", m.ReplyTo);

        var reset = EmailTemplates.PasswordReset("a@b.com", "Site", "https://x/r?t=1&u=2");
        Assert.Contains("href=\"https://x/r?t=1&amp;u=2\"", reset.Html);
    }

    [Fact]
    public void Smtp_message_comes_from_the_site()
    {
        var settings = new EmailSettings { Host = "smtp.example.com", FromAddress = "no-reply@example.com" };
        Assert.True(settings.IsConfigured);
        Assert.False(new EmailSettings { Host = "smtp.example.com" }.IsConfigured);
        var mime = SmtpEmailSender.Build(new EmailMessage("a@b.com", "Hi", "text", "<p>html</p>", "owner@b.com"), settings, "Smith Portfolios");
        Assert.Equal("\"Smith Portfolios\" <no-reply@example.com>", mime.From.ToString());
        Assert.Equal("owner@b.com", mime.ReplyTo.ToString());
        Assert.Equal("text", mime.TextBody);
        Assert.Equal("<p>html</p>", mime.HtmlBody);
        Assert.Equal(MailKit.Security.SecureSocketOptions.Auto, SmtpEmailSender.SocketOptions(null));
        Assert.Equal(MailKit.Security.SecureSocketOptions.StartTls, SmtpEmailSender.SocketOptions("StartTls"));
        Assert.Equal(MailKit.Security.SecureSocketOptions.None, SmtpEmailSender.SocketOptions("none"));
    }

    [Fact]
    public void Invoice_email_lists_lines_totals_and_how_to_pay()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("en-AU");
        try
        {
            var customer = new Customer(1, "Acme Pty Ltd", "Jo", "jo@acme.com", null, null, null, true);
            var invoice = new InvoiceDetail(5, "INV-0005", 1, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 15), "PO 7", null, false,
                InvoiceStatus.Approved, 100m, 10m, 110m, 9, customer,
                [new InvoiceLine(1, "Bookkeeping <Sept>", 2, 50m, 4800, 1, "GST", 100m, 10m, 110m)],
                [new InvoicePayment(1, new DateOnly(2026, 10, 5), 10m, 1100, "Bank", null, null)]);
            var settings = InvoiceSettings.Default with { PaymentDetails = "BSB 123-456\nAccount 12345678" };

            var m = InvoiceEmail.Build(invoice, settings, "Smith & Co", "GST", "jo@acme.com", "owner@smith.com", "Thanks!");

            Assert.Equal("Invoice INV-0005 from Smith & Co", m.Subject);
            Assert.StartsWith("Hi Jo,", m.Text);
            Assert.Contains("for $110.00 ($100.00 still to pay), due on 15/10/2026.", m.Text);
            Assert.Contains("Bookkeeping <Sept>  2 x $50.00  $110.00", m.Text);
            Assert.Contains("GST: $10.00", m.Text);
            Assert.Contains("To pay: $100.00", m.Text);
            Assert.Contains("BSB 123-456\nAccount 12345678", m.Text);
            Assert.Contains("Thanks!", m.Text);
            Assert.Contains("Bookkeeping &lt;Sept&gt;", m.Html);
            Assert.Contains("BSB 123-456<br>Account 12345678", m.Html);
            Assert.Equal("owner@smith.com", m.ReplyTo);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
