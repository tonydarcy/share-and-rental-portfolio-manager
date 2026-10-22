using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using PortfolioManager.Core.Accounts;
using PortfolioManager.Core.Email;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Account;

/// <summary>Sends the "check your email address" link again. Says the same thing whatever the email.</summary>
[AllowAnonymous]
[AllowViewOnly]
[EnableRateLimiting(SignInFlow.RateLimit)]
public class ResendConfirmationModel(AccountService accounts, AccountTokens tokens, IEmailSender email, Captcha captcha, SiteBranding site,
    ILogger<ResendConfirmationModel> logger) : PageModel
{
    [BindProperty(SupportsGet = true), Required, EmailAddress, Display(Name = "Email")]
    public string Email { get; set; } = "";

    public bool CanSendEmail => email.IsConfigured;
    public bool Sent { get; private set; }

    public void OnGet() => ModelState.Clear();

    public async Task<IActionResult> OnPostAsync()
    {
        if (!email.IsConfigured || !ModelState.IsValid)
            return Page();
        if (!await captcha.VerifyAsync(HttpContext))
        {
            ModelState.AddModelError(string.Empty, Captcha.Failed);
            return Page();
        }
        if (await accounts.FindByEmailAsync(Email) is { EmailConfirmed: false } user)
            await RegisterModel.SendConfirmationAsync(email, tokens, site, Request, Url, user, logger);
        Sent = true;
        return Page();
    }
}
