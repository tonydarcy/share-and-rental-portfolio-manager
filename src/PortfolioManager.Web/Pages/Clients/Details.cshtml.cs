using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Clients;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Clients;

/// <summary>The selected portfolio's client details. Only ever the portfolio from CurrentPortfolio.</summary>
public class DetailsModel(CurrentPortfolio current, ClientService clients) : PageModel
{
    /// <summary>Empty rows shown under the officeholders already saved.</summary>
    public const int SpareRows = 3;

    [BindProperty]
    public ClientDetailsInput Input { get; set; } = new();

    public PortfolioSummary? Portfolio { get; private set; }
    public ClientDetails? Details { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is null)
            return Page();

        Details = await clients.GetAsync(Portfolio.PortfolioId);
        if (Details is null)
            return NotFound();
        Input = ClientDetailsInput.From(Details);
        AddSpareRows();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is null)
            return Forbid();

        var result = await clients.SaveAsync(Portfolio.PortfolioId, CurrentPortfolio.UserId(User), Input);
        if (!result.Ok)
        {
            foreach (var error in Input.Validate().DefaultIfEmpty(result.Error!))
                ModelState.AddModelError(string.Empty, error);
            Details = await clients.GetAsync(Portfolio.PortfolioId);
            AddSpareRows();
            return Page();
        }

        TempData["Message"] = $"Saved the client details for {Input.LegalName.Trim()}.";
        return RedirectToPage();
    }

    private void AddSpareRows()
    {
        Input.Officeholders.RemoveAll(o => o.IsBlank);
        var spare = Math.Min(SpareRows, ClientDetailsInput.MaxOfficeholders - Input.Officeholders.Count);
        for (var i = 0; i < spare; i++)
            Input.Officeholders.Add(new OfficeholderInput());
    }
}
