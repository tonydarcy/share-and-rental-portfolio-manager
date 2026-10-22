using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using PortfolioManager.Core.Accounts;
using PortfolioManager.Core.Email;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Account;

[AllowAnonymous]
[AllowViewOnly]
[EnableRateLimiting(SignInFlow.RateLimit)]
public class RegisterModel(AccountService accounts, AccountTokens tokens, IEmailSender email, Captcha captcha, SiteBranding site,
    ILogger<RegisterModel> logger) : PageModel
{
    // Filled in from an invitation email's link
    [BindProperty(SupportsGet = true), Display(Name = "Email")]
    public string? Email { get; set; }

    [BindProperty, Display(Name = "Your name (optional)")]
    public string? ContactName { get; set; }

    [BindProperty, Display(Name = "Portfolio name")]
    public string? PortfolioName { get; set; } = "My Portfolio";

    [BindProperty, DataType(DataType.Password)]
    public string? Password { get; set; }

    [BindProperty, DataType(DataType.Password), Display(Name = "Confirm password")]
    public string? ConfirmPassword { get; set; }

    public IActionResult OnGet() =>
        User.Identity?.IsAuthenticated == true ? RedirectToPage("/Index") : Page();

    public async Task<IActionResult> OnPostAsync()
    {
        foreach (var error in AccountService.ValidateRegistration(Email, Password, ConfirmPassword))
            ModelState.AddModelError(string.Empty, error);
        if (string.IsNullOrWhiteSpace(PortfolioName) || PortfolioName.Trim().Length > 100)
            ModelState.AddModelError(string.Empty, "Enter a portfolio name (up to 100 characters).");
        if ((ContactName?.Length ?? 0) > 100)
            ModelState.AddModelError(string.Empty, "Your name can be at most 100 characters.");
        if (!ModelState.IsValid)
            return Page();
        if (!await captcha.VerifyAsync(HttpContext))
        {
            ModelState.AddModelError(string.Empty, Captcha.Failed);
            return Page();
        }

        if (!await accounts.RegisterAsync(Email!, Password!, PortfolioName!, ContactName))
        {
            ModelState.AddModelError(string.Empty, "An account with that email already exists. Log in instead, or use a different email.");
            return Page();
        }

        // With email settings, the new login can't be used until the person follows the emailed link
        if (email.IsConfigured && await accounts.FindByEmailAsync(Email!) is { EmailConfirmed: false } user)
        {
            if (await SendConfirmationAsync(email, tokens, site, Request, Url, user, logger))
            {
                TempData["Message"] = $"Your account has been created. We've emailed a link to {user.Email}: follow it to confirm your address, then log in.";
                return RedirectToPage("/Account/Login");
            }
            TempData["Error"] = "Your account has been created, but the email to confirm your address couldn't be sent. Try \"Send the link again\" in a few minutes.";
            return RedirectToPage("/Account/ResendConfirmation", new { email = user.Email });
        }

        TempData["Message"] = "Your account has been created. Log in to get started.";
        return RedirectToPage("/Account/Login");
    }

    /// <summary>Emails the link that confirms a login's address. False (and logged) if the email couldn't be sent.</summary>
    internal static async Task<bool> SendConfirmationAsync(IEmailSender email, AccountTokens tokens, SiteBranding site, HttpRequest request,
        IUrlHelper url, SignedInUser user, ILogger logger)
    {
        var link = site.Link(request, url.Page("/Account/ConfirmEmail", new { token = tokens.ConfirmEmail(user) })!);
        try
        {
            await email.SendAsync(EmailTemplates.ConfirmEmail(user.Email, site.Name, link));
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Couldn't send the email check to login {UserId}", user.UserId);
            return false;
        }
    }
}
