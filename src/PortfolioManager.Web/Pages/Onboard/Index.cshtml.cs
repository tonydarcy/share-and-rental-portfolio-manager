using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using PortfolioManager.Core.Practice;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Onboard;

/// <summary>The page a new client opens from the onboarding link: the engagement letter, the details form, and accepting. No sign-in.</summary>
[AllowAnonymous]
[EnableRateLimiting(SignInFlow.RateLimit)]
public class IndexModel(OnboardingService onboarding) : PageModel
{
    [BindProperty] public OnboardingDetails Details { get; set; } = new();
    [BindProperty] public string? AcceptedName { get; set; }
    [BindProperty] public bool Agree { get; set; }

    public Onboarding? Item { get; private set; }
    public bool Done { get; private set; }

    public async Task<IActionResult> OnGetAsync(string token)
    {
        Item = await onboarding.GetByTokenAsync(token);
        if (Item is null || Item.Status == OnboardingStatuses.Cancelled)
            return NotFound();
        Done = Item.Status != OnboardingStatuses.Sent;
        Details.LegalName ??= Item.ClientName;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string token)
    {
        Item = await onboarding.GetByTokenAsync(token);
        if (Item is null || Item.Status == OnboardingStatuses.Cancelled)
            return NotFound();
        if (Item.Status != OnboardingStatuses.Sent)
        {
            Done = true;
            return Page();
        }
        foreach (var e in Details.Validate(Item.EntityType))
            ModelState.AddModelError(string.Empty, e);
        if (!Agree || string.IsNullOrWhiteSpace(AcceptedName))
            ModelState.AddModelError(string.Empty, "Tick to agree, and type your name to accept the letter.");
        if (ModelState.ErrorCount > 0)
            return Page();
        var r = await onboarding.AcceptAsync(token, AcceptedName!, HttpContext.Connection.RemoteIpAddress?.ToString(), Details);
        if (!r.Ok)
        {
            ModelState.AddModelError(string.Empty, r.Error!);
            return Page();
        }
        Done = true;
        return Page();
    }
}
