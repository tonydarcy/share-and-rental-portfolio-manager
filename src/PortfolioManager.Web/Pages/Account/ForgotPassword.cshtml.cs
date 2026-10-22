using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using PortfolioManager.Core.Accounts;
using PortfolioManager.Core.Email;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Account;

/// <summary>Emails a link to choose a new password. It says the same thing whether or not the email has a login.</summary>
[AllowAnonymous]
[AllowViewOnly]
[EnableRateLimiting(SignInFlow.RateLimit)]
public class ForgotPasswordModel(AccountService accounts, AccountTokens tokens, IEmailSender email, Captcha captcha, SiteBranding site,
    ILogger<ForgotPasswordModel> logger) : PageModel
{
    [BindProperty, Required, EmailAddress, Display(Name = "Email")]
    public string Email { get; set; } = "";

    public bool CanSendEmail => email.IsConfigured;
    public bool Sent { get; private set; }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!email.IsConfigured || !ModelState.IsValid)
            return Page();
        if (!await captcha.VerifyAsync(HttpContext))
        {
            ModelState.AddModelError(string.Empty, Captcha.Failed);
            return Page();
        }

        if (await accounts.FindByEmailAsync(Email) is { } user)
        {
            var link = site.Link(Request, Url.Page("/Account/ResetPassword", new { token = tokens.PasswordReset(user) })!);
            try
            {
                await email.SendAsync(EmailTemplates.PasswordReset(user.Email, site.Name, link));
            }
            catch (Exception ex)
            {
                // Not shown, so the page doesn't reveal which emails have logins
                logger.LogError(ex, "Couldn't send a password reset email to login {UserId}", user.UserId);
            }
        }
        Sent = true;
        return Page();
    }
}
