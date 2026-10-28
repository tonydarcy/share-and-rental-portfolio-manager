using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Legal;

/// <summary>
/// The terms of use, privacy policy and disclaimer pages, open to everyone. They are missing when the host turns
/// them off (Legal:Enabled), and send people to the host's own page when one is set.
/// </summary>
[AllowAnonymous]
public abstract class LegalPageModel(LegalSettings legal, SiteBranding site) : PageModel
{
    public LegalSettings Legal => legal;
    public string Operator => legal.Operator(site);
    public string SiteName => site.Name;
    public string? Abn => string.IsNullOrWhiteSpace(legal.Abn) ? null : legal.Abn.Trim();
    public string? Contact => legal.Contact(site);

    /// <summary>"Example Pty Ltd (ABN 12 345 678 901)", or just the name.</summary>
    public string OperatorWithAbn => Abn is null ? Operator : $"{Operator} (ABN {Abn})";

    /// <summary>The host's own version of this page, if they have one.</summary>
    protected virtual string? OwnPageUrl => null;

    public IActionResult OnGet() =>
        !legal.Enabled ? NotFound()
        : !string.IsNullOrWhiteSpace(OwnPageUrl) ? Redirect(OwnPageUrl)
        : Page();
}

public class TermsModel(LegalSettings legal, SiteBranding site) : LegalPageModel(legal, site)
{
    protected override string? OwnPageUrl => Legal.TermsUrl;
}

public class PrivacyModel(LegalSettings legal, SiteBranding site) : LegalPageModel(legal, site)
{
    protected override string? OwnPageUrl => Legal.PrivacyUrl;
}

public class DisclaimerModel(LegalSettings legal, SiteBranding site) : LegalPageModel(legal, site);
