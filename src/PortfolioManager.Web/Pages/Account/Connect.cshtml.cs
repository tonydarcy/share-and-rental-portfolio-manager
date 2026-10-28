using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Security;
using PortfolioManager.Web.Api;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Account;

/// <summary>
/// The OAuth sign-in step: an app such as Claude sends the person here, they log in if they aren't already, and they
/// allow it (or not). Allowing sends the app back with a one-time code, which it swaps for an API key at /oauth/token.
/// </summary>
[AllowViewOnly]
public class ConnectModel(OAuthServer oauth, AccountSecurity security) : PageModel
{
    /// <summary>The fields of the app's request, carried through the form so the answer can be checked again.</summary>
    public static readonly string[] Fields =
        ["response_type", "client_id", "redirect_uri", "code_challenge", "code_challenge_method", "state", "scope", "resource"];

    [BindProperty]
    public bool ReadOnly { get; set; }

    public OAuthRequest? Asked { get; private set; }
    public string? Error { get; private set; }
    public Dictionary<string, string> Carried { get; } = [];

    public IActionResult OnGet() => Check(Request.Query.ToDictionary(q => q.Key, q => q.Value.ToString())) ?? Page();

    public async Task<IActionResult> OnPostAllowAsync()
    {
        if (Check(Form()) is { } stop)
            return stop;
        await security.AddAsync(HttpContext, CurrentPortfolio.UserId(User), User.Identity?.Name, SecurityEvents.AppConnected);
        var code = oauth.IssueCode(Asked!, CurrentPortfolio.UserId(User), ReadOnly);
        return Redirect(OAuthServer.RedirectWith(Asked!.RedirectUri, ("code", code), ("state", Asked.State)));
    }

    public IActionResult OnPostDeny()
    {
        if (Check(Form()) is { } stop)
            return stop;
        return Redirect(OAuthServer.RedirectWith(Asked!.RedirectUri,
            ("error", "access_denied"), ("error_description", "The person said no."), ("state", Asked.State)));
    }

    private Dictionary<string, string> Form() => Fields.ToDictionary(f => f, f => Request.Form[f].ToString());

    /// <summary>Null when the request is good; otherwise the page with the problem, or the app told of it.</summary>
    private IActionResult? Check(Dictionary<string, string> values)
    {
        // Another site can't show this page in a frame and trick a click on Allow
        Response.Headers.XFrameOptions = "DENY";
        foreach (var f in Fields)
            if (values.TryGetValue(f, out var v) && v.Length > 0)
                Carried[f] = v;

        var (asked, problem) = oauth.Check(f => Carried.GetValueOrDefault(f));
        Asked = asked;
        if (problem is null)
            return null;
        if (problem.Redirect && asked is not null)
            return Redirect(OAuthServer.RedirectWith(asked.RedirectUri,
                ("error", problem.Error), ("error_description", problem.Description), ("state", asked.State)));
        Error = problem.Description;
        Asked = null;
        return Page();
    }
}
