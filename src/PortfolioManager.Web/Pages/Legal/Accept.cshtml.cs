using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Legal;

/// <summary>Asks a signed-in person to agree to the current terms of use and privacy policy (new or changed since they last did).</summary>
[AllowViewOnly]
public class AcceptModel(LegalSettings legal, TermsFilter terms) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    [BindProperty]
    public bool Agree { get; set; }

    /// <summary>True when they agreed to an earlier version, so the page says the terms have changed.</summary>
    public bool Updated { get; private set; }

    public string? TermsUrl => string.IsNullOrWhiteSpace(legal.TermsUrl) ? null : legal.TermsUrl;
    public string? PrivacyUrl => string.IsNullOrWhiteSpace(legal.PrivacyUrl) ? null : legal.PrivacyUrl;

    public async Task<IActionResult> OnGetAsync()
    {
        if (!legal.AsksForAcceptance)
            return Back();
        var accepted = await terms.AcceptedVersionAsync(CurrentPortfolio.UserId(User));
        if (accepted == legal.Version)
            return Back();
        Updated = accepted is not null;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!legal.AsksForAcceptance)
            return Back();
        if (!Agree)
        {
            ModelState.AddModelError(nameof(Agree), "Tick the box to agree, or log out if you don't want to.");
            return Page();
        }
        await terms.AcceptAsync(CurrentPortfolio.UserId(User), HttpContext.Connection.RemoteIpAddress?.ToString());
        return Back();
    }

    // Only ever back into this site
    private IActionResult Back() => Url.IsLocalUrl(ReturnUrl) ? LocalRedirect(ReturnUrl) : RedirectToPage("/Index");
}
