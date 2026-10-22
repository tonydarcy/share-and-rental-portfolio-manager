using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Email;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Portfolio;

/// <summary>Who else can open the picked portfolio. The procs check the signed-in login is one of its owners.</summary>
public class SharingModel(CurrentPortfolio current, PortfolioService portfolios, IEmailSender email, SiteBranding site,
    ILogger<SharingModel> logger) : PageModel
{
    [BindProperty]
    public string? Email { get; set; }

    /// <summary>"View", "Edit" or "Full".</summary>
    [BindProperty]
    public string Access { get; set; } = "Edit";

    public PortfolioSummary? Portfolio { get; private set; }
    public IReadOnlyList<PortfolioShare> Shares { get; private set; } = [];
    public IReadOnlyList<PortfolioInvite> Invites { get; private set; } = [];
    public int UserId { get; private set; }

    /// <summary>The sign-up page, to send to someone who has been invited.</summary>
    public string SignUpUrl => site.Link(Request, Url.Page("/Account/Register") ?? "/Account/Register");

    public bool CanSendEmail => email.IsConfigured;

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostShareAsync()
    {
        await LoadAsync();
        if (Portfolio is null)
            return RedirectToPage();
        if (PortfolioService.ValidateShare(Email, Access) is { } error)
        {
            ModelState.AddModelError(nameof(Email), error);
            return Page();
        }

        var access = PortfolioService.ParseChoice(Access)!.Value;
        var share = await portfolios.ShareAsync(UserId, Portfolio.PortfolioId, Email!, access);
        if (!share.Result.Ok)
        {
            ModelState.AddModelError(nameof(Email), share.Result.Error!);
            return Page();
        }
        var what = PortfolioService.AccessLabel(access).ToLowerInvariant();
        if (email.IsConfigured)
        {
            var to = Email!.Trim();
            var link = share.Invited ? site.Link(Request, Url.Page("/Account/Register", new { email = to })!) : site.Link(Request, "/");
            var role = access switch { PortfolioAccess.View => "read only", PortfolioAccess.Edit => "an editor", _ => "an owner" };
            try
            {
                await email.SendAsync(EmailTemplates.Invitation(to, site.Name, User.Identity?.Name ?? "Someone", Portfolio.Name, role, link, !share.Invited));
                TempData["Message"] = share.Invited
                    ? $"Nobody has signed up with {to} yet, so an invitation was saved and emailed to them. Once they sign up with that email and log in, \"{Portfolio.Name}\" opens for them as {what}."
                    : $"{to} is now {(access == PortfolioAccess.Edit ? "an" : "a")} {what} of \"{Portfolio.Name}\", and we've emailed to let them know.";
                return RedirectToPage();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Couldn't email a sharing invitation for portfolio {PortfolioId}", Portfolio.PortfolioId);
                TempData["Error"] = "The portfolio is shared, but the email to let them know couldn't be sent. Send them the link below yourself.";
            }
        }
        TempData["Message"] = share.Invited
            ? $"Nobody has signed up with {Email!.Trim()} yet, so an invitation was saved. Send them the sign-up link below: once they sign up with that email and log in, \"{Portfolio.Name}\" opens for them as {what}."
            : $"{Email!.Trim()} is now {(access == PortfolioAccess.Edit ? "an" : "a")} {what} of \"{Portfolio.Name}\". It shows in their portfolio list next time they log in or open a page.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRemoveAsync(int permissionId)
    {
        if (await current.GetSelectedAsync() is { } portfolio)
        {
            var result = await portfolios.RemoveShareAsync(CurrentPortfolio.UserId(User), portfolio.PortfolioId, permissionId);
            TempData[result.Ok ? "Message" : "Error"] = result.Ok ? "Access removed." : result.Error;
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostCancelInviteAsync(int inviteId)
    {
        if (await current.GetSelectedAsync() is { } portfolio)
        {
            var result = await portfolios.CancelInviteAsync(CurrentPortfolio.UserId(User), portfolio.PortfolioId, inviteId);
            TempData[result.Ok ? "Message" : "Error"] = result.Ok ? "Invitation cancelled." : result.Error;
        }
        return RedirectToPage();
    }

    private async Task LoadAsync()
    {
        UserId = CurrentPortfolio.UserId(User);
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is { CanShare: true })
        {
            Shares = await portfolios.GetSharesAsync(UserId, Portfolio.PortfolioId);
            Invites = await portfolios.GetInvitesAsync(UserId, Portfolio.PortfolioId);
        }
    }
}
