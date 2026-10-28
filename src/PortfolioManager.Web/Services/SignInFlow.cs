using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using PortfolioManager.Core.Accounts;
using PortfolioManager.Core.Portfolios;

namespace PortfolioManager.Web.Services;

/// <summary>The last step of logging in, after the password (and the authenticator code, if turned on).</summary>
public sealed class SignInFlow(PortfolioService portfolios, AccountSecurity security)
{
    /// <summary>The short-lived cookie that carries a login from the password page to the code page.</summary>
    public const string TwoFactorCookie = "pm_2fa";

    /// <summary>The rate limit on the login, sign-up and email pages (see Program.cs).</summary>
    public const string RateLimit = "signin";

    /// <summary>
    /// Signs in with the cookie and logs it (emailing the owner when the device is new, unless notifyNewDevice is false).
    /// Returns a message to show when shared portfolios were just added.
    /// </summary>
    public async Task<string?> SignInAsync(HttpContext context, SignedInUser user, bool rememberMe, bool notifyNewDevice = true)
    {
        context.Response.Cookies.Delete(TwoFactorCookie);

        // Portfolios shared with this email before the person signed up open from now on
        var message = await portfolios.AcceptInvitesAsync(user.UserId) > 0
            ? "Portfolios shared with you are now in your portfolio list."
            : null;

        await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(AccountSecurity.Identity(user)),
            new AuthenticationProperties { IsPersistent = rememberMe });
        await security.SignedInAsync(context, user, notifyNewDevice);
        return message;
    }

    /// <summary>Re-issues this browser's sign-in cookie after the login's two-factor setting changed, keeping "keep me signed in".</summary>
    public static async Task RefreshAsync(HttpContext context, SignedInUser user)
    {
        var current = await context.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        var email = context.User.FindFirstValue(ClaimTypes.Name) ?? user.Email;
        await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(AccountSecurity.Identity(user with { Email = email })),
            new AuthenticationProperties { IsPersistent = current.Properties?.IsPersistent == true });
    }
}
