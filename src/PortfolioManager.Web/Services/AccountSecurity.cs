using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using PortfolioManager.Core.Accounts;
using PortfolioManager.Core.Email;
using PortfolioManager.Core.Security;

namespace PortfolioManager.Web.Services;

/// <summary>
/// The web side of the security log: where a request came from, the account-paused and new-device emails,
/// and the check that signs a browser out once its password has changed.
/// </summary>
public sealed class AccountSecurity(SecurityLogService log, IEmailSender email, SiteBranding site, ILogger<AccountSecurity> logger)
{
    /// <summary>A random id kept in each browser for two years, so a sign-in from a new device can be spotted.</summary>
    public const string DeviceCookie = "pm_device";

    /// <summary>The password stamp the sign-in cookie was issued with; it no longer matches once the password changes.</summary>
    public const string StampClaim = "pm_stamp";

    /// <summary>"1" when the login has two-factor sign-in on, refreshed with the stamp check.</summary>
    public const string TwoFactorClaim = "pm_2fa";

    /// <summary>How often a signed-in browser's password stamp is checked against the database.</summary>
    public static readonly TimeSpan CheckEvery = TimeSpan.FromMinutes(1);
    private const string CheckedKey = "pm_checked";

    public const string PausedMessage = "Too many wrong attempts for this account, so sign-in is paused for 15 minutes. "
        + "If this wasn't you, reset your password and turn on two-factor sign-in.";

    public static SecurityOrigin Origin(HttpContext context) => new(
        context.Connection.RemoteIpAddress?.ToString(),
        context.Request.Headers.UserAgent.ToString() is { Length: > 0 } ua ? ua : null,
        DeviceId(context));

    /// <summary>The browser's device id, made the first time it is needed.</summary>
    public static string DeviceId(HttpContext context)
    {
        if (context.Request.Cookies[DeviceCookie] is { Length: >= 16 } existing)
            return existing;
        if (context.Items[DeviceCookie] is string made)
            return made;
        var id = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)).Replace('+', '-').Replace('/', '_');
        context.Items[DeviceCookie] = id;
        context.Response.Cookies.Append(DeviceCookie, id, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            MaxAge = TimeSpan.FromDays(730),
            IsEssential = true,
        });
        return id;
    }

    public Task AddAsync(HttpContext context, int? userId, string? emailAddress, string evt, bool success = true) =>
        log.AddAsync(userId, emailAddress, evt, success, Origin(context));

    public async Task<bool> IsPausedAsync(string emailAddress) =>
        SignInLockout.IsPaused(await log.RecentFailuresAsync(emailAddress));

    /// <summary>
    /// Records a wrong password or two-factor code. When it is the one that pauses the account, logs that and
    /// emails the owner (if the email belongs to a login). Returns true when sign-in is now paused.
    /// </summary>
    public async Task<bool> FailedAsync(HttpContext context, string evt, string emailAddress, SignedInUser? owner)
    {
        await AddAsync(context, owner?.UserId, emailAddress, evt, success: false);
        var failures = await log.RecentFailuresAsync(emailAddress);
        if (SignInLockout.JustPaused(failures) && owner is not null)
        {
            await AddAsync(context, owner.UserId, emailAddress, SecurityEvents.Paused);
            var origin = Origin(context);
            await SendAsync(owner.Email, $"{site.Name}: sign-in paused after wrong attempts",
                $"Someone typed a wrong password or code for your {site.Name} account {SignInLockout.MaxFailures} times, "
                + $"so sign-in is paused for {SignInLockout.Window.TotalMinutes:0} minutes.\n\n"
                + $"From: {origin.IpAddress}\nBrowser: {origin.UserAgent}\n\n"
                + "If this was you, wait and try again. If it wasn't, reset your password (Forgot password on the login page) "
                + "and turn on two-factor sign-in.");
        }
        return SignInLockout.IsPaused(failures);
    }

    /// <summary>Logs a finished sign-in, and emails the owner when it came from a device they haven't used before.</summary>
    public async Task SignedInAsync(HttpContext context, SignedInUser user, bool notifyNewDevice)
    {
        var origin = Origin(context);
        var isNew = notifyNewDevice && await log.IsNewDeviceAsync(user.UserId, origin.DeviceId);
        await log.AddAsync(user.UserId, user.Email, SecurityEvents.SignedIn, true, origin);
        if (isNew)
            await SendAsync(user.Email, $"{site.Name}: new sign-in to your account",
                $"Your {site.Name} account was just signed in to from a browser or device it hasn't seen before.\n\n"
                + $"When: {DateTime.UtcNow:d MMM yyyy HH:mm} UTC\nFrom: {origin.IpAddress}\nBrowser: {origin.UserAgent}\n\n"
                + "If this was you, there's nothing to do. If it wasn't, reset your password straight away and turn on "
                + "two-factor sign-in. Account > Recent sign-ins lists every sign-in.");
    }

    private async Task SendAsync(string to, string subject, string text)
    {
        if (!email.IsConfigured || string.IsNullOrWhiteSpace(to))
            return;
        try
        {
            await email.SendAsync(new EmailMessage(to, subject, text));
        }
        catch (Exception ex)
        {
            // A warning email that can't be sent mustn't stop the sign-in
            logger.LogWarning(ex, "Couldn't send the security email \"{Subject}\"", subject);
        }
    }

    /// <summary>The claims a sign-in cookie carries.</summary>
    public static ClaimsIdentity Identity(SignedInUser user) => new(
        [
            new Claim(ClaimTypes.NameIdentifier, user.UserId.ToString()),
            new Claim(ClaimTypes.Name, user.Email),
            new Claim(StampClaim, user.PasswordStamp),
            new Claim(TwoFactorClaim, user.HasTwoFactor ? "1" : "0"),
        ],
        CookieAuthenticationDefaults.AuthenticationScheme);

    public static bool HasTwoFactor(ClaimsPrincipal user) => user.FindFirstValue(TwoFactorClaim) == "1";

    /// <summary>True when the stamp hasn't been checked within <see cref="CheckEvery"/>.</summary>
    public static bool NeedsCheck(DateTimeOffset? lastChecked, DateTimeOffset now) =>
        lastChecked is not { } last || now - last >= CheckEvery || now < last;

    /// <summary>
    /// The sign-in cookie's check (Program.cs, OnValidatePrincipal): once a minute, sign the browser out if the
    /// login is gone or its password has changed since this cookie was issued (a reset elsewhere signs out every
    /// browser), and keep the two-factor flag up to date. Cookies from before this check have no stamp and sign out once.
    /// </summary>
    public static async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        var now = context.HttpContext.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
        var lastChecked = context.Properties.Items.TryGetValue(CheckedKey, out var text)
            && long.TryParse(text, out var ticks) ? new DateTimeOffset(ticks, TimeSpan.Zero) : (DateTimeOffset?)null;
        if (!NeedsCheck(lastChecked, now))
            return;

        var principal = context.Principal;
        var stamp = principal?.FindFirstValue(StampClaim);
        var user = int.TryParse(principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)
            ? await context.HttpContext.RequestServices.GetRequiredService<AccountService>().FindByIdAsync(userId)
            : null;
        if (user is null || string.IsNullOrEmpty(stamp) || user.PasswordStamp != stamp)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return;
        }

        if (HasTwoFactor(principal!) != user.HasTwoFactor)
            context.ReplacePrincipal(new ClaimsPrincipal(Identity(user with { Email = principal!.FindFirstValue(ClaimTypes.Name) ?? user.Email })));
        context.Properties.Items[CheckedKey] = now.UtcTicks.ToString();
        context.ShouldRenew = true;
    }
}
