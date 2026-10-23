using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Email;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Account;

/// <summary>Lets the site administrator see the Email settings in use (never the password) and send a test email.</summary>
// Sending a test changes nothing, so it's allowed whatever access the picked portfolio gives
[AllowViewOnly]
public class TestEmailModel(SiteAdmin admin, IEmailSender email, EmailSettings settings, SiteBranding site,
    ILogger<TestEmailModel> logger) : PageModel
{
    public EmailSettings Settings => settings;
    public bool EmailOn => email.IsConfigured;
    public string? BaseUrl => site.BaseUrl;

    [BindProperty]
    [Required, EmailAddress, Display(Name = "Send the test to")]
    public string? To { get; set; }

    public string? Sent { get; private set; }
    public string? Problem { get; private set; }

    public IActionResult OnGet()
    {
        if (!admin.IsAdmin(User))
            return Forbid();
        To = User.FindFirstValue(ClaimTypes.Name);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (!admin.IsAdmin(User))
            return Forbid();
        if (!email.IsConfigured || !ModelState.IsValid)
            return Page();

        try
        {
            await email.SendAsync(EmailTemplates.Test(To!, site.Name, site.Link(Request, Url.Page("/Index")!)), ct);
            Sent = To;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The test email couldn't be sent");
            Problem = EmailProblem.Describe(ex);
        }
        return Page();
    }
}
