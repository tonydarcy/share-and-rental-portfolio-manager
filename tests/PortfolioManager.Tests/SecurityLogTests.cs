using System.Security.Claims;
using PortfolioManager.Core.Accounts;
using PortfolioManager.Core.Security;
using PortfolioManager.Web.Pages.Account;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Tests;

public class SecurityLogTests
{
    private static object? Get(object p, string name) => p.GetType().GetProperty(name)!.GetValue(p);

    [Theory]
    [InlineData(0, false)]
    [InlineData(4, false)]
    [InlineData(5, true)]
    [InlineData(9, true)]
    public void Sign_in_pauses_after_five_wrong_attempts(int failures, bool paused) =>
        Assert.Equal(paused, SignInLockout.IsPaused(failures));

    [Fact]
    public void The_owner_is_emailed_once_when_the_pause_starts()
    {
        Assert.False(SignInLockout.JustPaused(4));
        Assert.True(SignInLockout.JustPaused(5));
        Assert.False(SignInLockout.JustPaused(6));
    }

    [Fact]
    public void Log_entries_keep_the_email_in_lower_case_and_never_the_raw_device_cookie()
    {
        var origin = new SecurityOrigin("203.0.113.9", new string('x', 400), "device-cookie-value");
        var p = SecurityLogService.Parameters(7, "  Tony@Example.COM ", SecurityEvents.Password, false, origin);

        Assert.Equal(7, Get(p, "iUserId"));
        Assert.Equal("tony@example.com", Get(p, "strEmail"));
        Assert.Equal("Password", Get(p, "strEvent"));
        Assert.Equal(false, Get(p, "bSuccess"));
        Assert.Equal("203.0.113.9", Get(p, "strIpAddress"));
        Assert.Equal(SecurityLogService.MaxUserAgent, ((string)Get(p, "strUserAgent")!).Length);
        Assert.Equal(32, ((string)Get(p, "strDevice")!).Length);
        Assert.DoesNotContain("device-cookie", (string)Get(p, "strDevice")!);
    }

    [Fact]
    public void A_missing_email_or_device_is_stored_as_null()
    {
        var p = SecurityLogService.Parameters(null, " ", SecurityEvents.ApiKeyCreated, true, new SecurityOrigin(null, null, null));
        Assert.Null(Get(p, "strEmail"));
        Assert.Null(Get(p, "strDevice"));
        Assert.Null(SecurityLogService.DeviceHash(""));
    }

    [Fact]
    public void The_same_device_always_hashes_the_same() =>
        Assert.Equal(SecurityLogService.DeviceHash("abc"), SecurityLogService.DeviceHash("abc"));

    [Fact]
    public void Sign_in_cookies_carry_the_password_stamp_and_two_factor_flag()
    {
        var identity = AccountSecurity.Identity(new SignedInUser(12, "a@b.com", TwoFactorSecret: "secret", PasswordStamp: "ABCD"));
        var principal = new ClaimsPrincipal(identity);

        Assert.Equal("12", principal.FindFirstValue(ClaimTypes.NameIdentifier));
        Assert.Equal("ABCD", principal.FindFirstValue(AccountSecurity.StampClaim));
        Assert.True(AccountSecurity.HasTwoFactor(principal));
        Assert.False(AccountSecurity.HasTwoFactor(new ClaimsPrincipal(AccountSecurity.Identity(new SignedInUser(12, "a@b.com")))));
    }

    [Fact]
    public void The_password_stamp_is_checked_once_a_minute()
    {
        var now = new DateTimeOffset(2026, 10, 28, 10, 0, 0, TimeSpan.Zero);
        Assert.True(AccountSecurity.NeedsCheck(null, now));
        Assert.False(AccountSecurity.NeedsCheck(now.AddSeconds(-30), now));
        Assert.True(AccountSecurity.NeedsCheck(now.AddMinutes(-1), now));
        // A clock that went backwards checks again rather than trusting the old time
        Assert.True(AccountSecurity.NeedsCheck(now.AddMinutes(5), now));
    }

    [Fact]
    public void A_password_change_gives_a_new_stamp() =>
        Assert.NotEqual(AccountService.StampOf(PasswordHasher.HashPassword("first password")),
            AccountService.StampOf(PasswordHasher.HashPassword("second password")));

    [Fact]
    public void Pages_send_headers_that_stop_framing_and_outside_scripts()
    {
        var csp = SecurityHeaders.ContentSecurityPolicy(oauthConsent: false);
        Assert.Contains("frame-ancestors 'none'", csp);
        Assert.Contains("object-src 'none'", csp);
        Assert.Contains("base-uri 'self'", csp);
        Assert.Contains("form-action 'self';", csp);
        Assert.Contains("script-src 'self' 'unsafe-inline' https://cdn.jsdelivr.net https://challenges.cloudflare.com", csp);
        Assert.DoesNotContain("http://localhost", csp);
    }

    [Fact]
    public void The_app_connect_page_may_send_people_back_to_the_app() =>
        Assert.Contains("form-action 'self' https: http://localhost:* http://127.0.0.1:*", SecurityHeaders.ContentSecurityPolicy(oauthConsent: true));

    [Theory]
    [InlineData("/Account/TwoFactor", true)]
    [InlineData("/Account/Logout", true)]
    [InlineData("/Error", true)]
    [InlineData("/Index", false)]
    [InlineData("/Import/Prices", false)]
    [InlineData(null, false)]
    public void Admins_without_two_factor_can_still_reach_their_account_pages(string? page, bool allowed) =>
        Assert.Equal(allowed, AdminTwoFactorFilter.IsAccountPage(page));

    [Fact]
    public void Admins_need_two_factor_unless_the_host_turns_it_off() =>
        Assert.True(new SecuritySettings().RequireTwoFactorForAdmins);

    [Theory]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36 Edg/130.0", "Edge on Windows")]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1", "Safari on iPhone or iPad")]
    [InlineData("Mozilla/5.0 (Macintosh; Intel Mac OS X 14.0; rv:131.0) Gecko/20100101 Firefox/131.0", "Firefox on Mac")]
    [InlineData("curl/8.5", "curl/8.5")]
    [InlineData(null, "")]
    public void Recent_sign_ins_name_the_browser(string? userAgent, string expected) =>
        Assert.Equal(expected, ActivityModel.Browser(userAgent));
}
