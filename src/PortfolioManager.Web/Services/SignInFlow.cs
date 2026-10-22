using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using PortfolioManager.Core.Accounts;
using PortfolioManager.Core.Portfolios;

namespace PortfolioManager.Web.Services;

/// <summary>The last step of logging in, after the password (and the authenticator code, if turned on).</summary>
public sealed class SignInFlow(PortfolioService portfolios)
{
    /// <summary>The short-lived cookie that carries a login from the password page to the code page.</summary>
    public const string TwoFactorCookie = "pm_2fa";

    /// <summary>The rate limit on the login, sign-up and email pages (see Program.cs).</summary>
    public const string RateLimit = "signin";

    /// <summary>Signs in with the cookie. Returns a message to show when shared portfolios were just added.</summary>
    public async Task<string?> SignInAsync(HttpContext context, SignedInUser user, bool rememberMe)
    {
        context.Response.Cookies.Delete(TwoFactorCookie);

        // Portfolios shared with this email before the person signed up open from now on
        var message = await portfolios.AcceptInvitesAsync(user.UserId) > 0
            ? "Portfolios shared with you are now in your portfolio list."
            : null;

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, user.UserId.ToString()), new Claim(ClaimTypes.Name, user.Email)],
            CookieAuthenticationDefaults.AuthenticationScheme);
        await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = rememberMe });
        return message;
    }
}
