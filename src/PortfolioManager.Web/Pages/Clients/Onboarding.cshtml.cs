using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Clients;
using PortfolioManager.Core.Email;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Core.Practice;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Clients;

/// <summary>
/// Sends a new client an engagement letter and details form by a link, then sets up their portfolio from the answers.
/// The onboardings belong to the login, not to a portfolio.
/// </summary>
[AllowViewOnly]
public class OnboardingModel(OnboardingService onboarding, ClientService clients, CurrentPortfolio current, IEmailSender email, SiteBranding site,
    ILogger<OnboardingModel> logger) : PageModel
{
    [BindProperty] public string? ClientName { get; set; }
    [BindProperty] public string? ClientEmail { get; set; }
    [BindProperty(SupportsGet = true)] public string EntityType { get; set; } = ClientEntityTypes.Individual;
    [BindProperty] public string? Letter { get; set; }
    [TempData] public string? Message { get; set; }
    [TempData] public string? Error { get; set; }
    [TempData] public string? NewLink { get; set; }

    public IReadOnlyList<Onboarding> Onboardings { get; private set; } = [];

    public async Task OnGetAsync()
    {
        if (!ClientEntityTypes.All.Contains(EntityType))
            EntityType = ClientEntityTypes.Individual;
        var me = CurrentPortfolio.UserId(User);
        Letter = EngagementLetters.Default(EntityType, await clients.GetPracticeAsync(me), "[client name]");
        Onboardings = await onboarding.ListAsync(me);
    }

    public string Link(Onboarding o) => site.Link(Request, Url.Page("/Onboard/Index", new { token = o.Token })!);

    public async Task<IActionResult> OnPostSendAsync()
    {
        var me = CurrentPortfolio.UserId(User);
        var name = ClientName?.Trim() ?? "";
        var letter = (Letter ?? "").Replace("[client name]", name);
        var (result, token) = await onboarding.CreateAsync(me, name, ClientEmail ?? "", EntityType, letter);
        if (!result.Ok)
        {
            Error = result.Error;
            return RedirectToPage(new { entityType = EntityType });
        }
        var link = site.Link(Request, Url.Page("/Onboard/Index", new { token })!);
        if (email.IsConfigured)
        {
            try
            {
                var practice = await clients.GetPracticeAsync(me);
                await email.SendAsync(EmailTemplates.Simple(ClientEmail!.Trim(), $"Welcome to {practice?.PracticeName ?? site.Name}",
                    [$"Dear {name},", $"{practice?.PracticeName ?? User.Identity?.Name} has sent you an engagement letter to read and accept, and a short form for your details.",
                     "The link is just for you; please don't forward it."], "Read the letter", link, User.Identity?.Name));
                Message = $"Sent to {ClientEmail.Trim()}.";
                return RedirectToPage();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Couldn't email an onboarding link");
                Error = "Saved, but the email couldn't be sent. Send the client the link below yourself.";
            }
        }
        Message ??= "Saved.";
        NewLink = link;
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostSetUpAsync(int id, string access)
    {
        var me = CurrentPortfolio.UserId(User);
        if ((await onboarding.ListAsync(me)).FirstOrDefault(o => o.Id == id) is not { } o)
            return NotFound();
        var r = await onboarding.SetUpAsync(me, o, access == "Edit" ? PortfolioAccess.Edit : PortfolioAccess.View);
        if (!r.Ok)
        {
            Error = r.Error;
            return RedirectToPage();
        }
        current.Refresh();
        await current.TrySelectAsync(r.Id!.Value);
        if (email.IsConfigured)
        {
            try
            {
                await email.SendAsync(EmailTemplates.Simple(o.ClientEmail, $"Your portfolio is ready on {site.Name}",
                    [$"Dear {o.ClientName},", "Your accountant has set up your portfolio. Sign up (or log in) with this email address to open it."],
                    "Open it", site.Link(Request, Url.Page("/Account/Register", new { email = o.ClientEmail })!), User.Identity?.Name));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Couldn't email an onboarded client");
            }
        }
        TempData["Message"] = $"{o.ClientName} is set up, and {o.ClientEmail} can open it. {r.Error}".Trim();
        return RedirectToPage("/Clients/Details");
    }

    public async Task<IActionResult> OnPostCancelAsync(int id)
    {
        var r = await onboarding.CancelAsync(CurrentPortfolio.UserId(User), id);
        if (r.Ok) Message = "Cancelled. The link no longer works."; else Error = r.Error;
        return RedirectToPage();
    }
}
