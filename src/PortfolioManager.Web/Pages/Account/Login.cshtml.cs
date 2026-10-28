using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using PortfolioManager.Core.Accounts;
using PortfolioManager.Core.Email;
using PortfolioManager.Core.Security;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Account;

[AllowAnonymous]
[AllowViewOnly]
[EnableRateLimiting(SignInFlow.RateLimit)]
public class LoginModel(AccountService accounts, SignInFlow signIn, AccountTokens tokens, IEmailSender email, Captcha captcha,
    SiteBranding site, AccountSecurity security) : PageModel
{
    [BindProperty, Required, EmailAddress, Display(Name = "Email")]
    public string Email { get; set; } = "";

    [BindProperty, Required, DataType(DataType.Password)]
    public string Password { get; set; } = "";

    [BindProperty, Display(Name = "Keep me signed in")]
    public bool RememberMe { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    /// <summary>Set when the password was right but the email hasn't been checked yet.</summary>
    public bool NeedsEmailCheck { get; private set; }

    public bool CanSendEmail => email.IsConfigured;

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
            return Page();
        if (!await captcha.VerifyAsync(HttpContext))
        {
            ModelState.AddModelError(string.Empty, Captcha.Failed);
            return Page();
        }

        // Five wrong passwords or codes for one email pause it for 15 minutes, wherever the guesses come from
        if (await security.IsPausedAsync(Email))
        {
            ModelState.AddModelError(string.Empty, AccountSecurity.PausedMessage);
            return Page();
        }

        var user = await accounts.SignInAsync(Email, Password);
        if (user is null)
        {
            var paused = await security.FailedAsync(HttpContext, SecurityEvents.Password, Email, await accounts.FindByEmailAsync(Email));
            ModelState.AddModelError(string.Empty, paused ? AccountSecurity.PausedMessage : "Email and/or password is incorrect.");
            return Page();
        }
        await security.AddAsync(HttpContext, user.UserId, Email, SecurityEvents.Password);

        // Once the site can send email, new logins confirm their address before they can be used
        if (email.IsConfigured && !user.EmailConfirmed)
        {
            NeedsEmailCheck = true;
            ModelState.AddModelError(string.Empty, "Please confirm your email address first, using the link we emailed you.");
            return Page();
        }

        if (user.HasTwoFactor)
        {
            Response.Cookies.Append(SignInFlow.TwoFactorCookie, tokens.TwoFactorPending(user, RememberMe), new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Lax,
                MaxAge = AccountTokens.TwoFactorLife,
            });
            return RedirectToPage("/Account/LoginTwoFactor", new { ReturnUrl });
        }

        if (await signIn.SignInAsync(HttpContext, user, RememberMe) is { } message)
            TempData["Message"] = message;

        // Only ever redirect back into this site
        return Url.IsLocalUrl(ReturnUrl) ? LocalRedirect(ReturnUrl) : RedirectToPage("/Index");
    }

    /// <summary>The login page's demo button (Site:DemoEmail and DemoPassword), which skips the CAPTCHA.</summary>
    public async Task<IActionResult> OnPostDemoAsync()
    {
        ModelState.Clear();
        if (!site.HasDemo || await accounts.SignInAsync(site.DemoEmail!, site.DemoPassword!) is not { } user)
        {
            ModelState.AddModelError(string.Empty, "The demo portfolio isn't available.");
            return Page();
        }
        await signIn.SignInAsync(HttpContext, user, rememberMe: false, notifyNewDevice: false);
        return RedirectToPage("/Index");
    }
}
