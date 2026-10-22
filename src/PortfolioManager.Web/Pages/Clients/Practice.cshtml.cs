using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Clients;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Clients;

/// <summary>The signed-in login's own practice details; they sign off every client's compilation report.</summary>
// Practice details belong to the login, not the portfolio
[AllowViewOnly]
public class PracticeModel(ClientService clients) : PageModel
{
    [BindProperty]
    public PracticeDetailsInput Input { get; set; } = new();

    public bool Saved { get; private set; }

    public async Task OnGetAsync()
    {
        if (await clients.GetPracticeAsync(CurrentPortfolio.UserId(User)) is { } practice)
        {
            Input = PracticeDetailsInput.From(practice);
            Saved = true;
        }
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var result = await clients.SavePracticeAsync(CurrentPortfolio.UserId(User), Input);
        if (!result.Ok)
        {
            foreach (var error in Input.Validate().DefaultIfEmpty(result.Error!))
                ModelState.AddModelError(string.Empty, error);
            return Page();
        }

        TempData["Message"] = "Saved your practice details.";
        return RedirectToPage();
    }
}
