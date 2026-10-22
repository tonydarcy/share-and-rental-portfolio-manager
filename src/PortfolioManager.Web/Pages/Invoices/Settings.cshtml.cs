using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Invoicing;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Invoices;

/// <summary>Invoice numbering, payment terms and the bank details printed on each invoice.</summary>
public class SettingsModel(CurrentPortfolio current, InvoiceService invoices, InvoiceExtrasService extras) : PageModel
{
    public bool HasLogo { get; private set; }

    /// <summary>The logo picture, for the invoice page and this page.</summary>
    public async Task<IActionResult> OnGetLogoAsync()
    {
        if (await current.GetSelectedAsync() is not { } p || await extras.GetLogoAsync(p.PortfolioId) is not { } logo)
            return NotFound();
        Response.Headers.XContentTypeOptions = "nosniff";
        return File(logo.Data, logo.Type);
    }

    public async Task<IActionResult> OnPostLogoAsync(IFormFile? logo, bool remove)
    {
        if (await current.GetSelectedAsync() is not { } p)
            return Forbid();
        byte[]? data = null;
        if (!remove)
        {
            if (logo is null || logo.Length == 0)
            {
                TempData["Error"] = "Choose a picture.";
                return RedirectToPage();
            }
            using var ms = new MemoryStream();
            await logo.CopyToAsync(ms);
            data = ms.ToArray();
        }
        var r = await extras.SaveLogoAsync(p.PortfolioId, logo?.FileName, data);
        TempData[r.Ok ? "Message" : "Error"] = r.Ok ? (remove ? "Logo removed." : "Logo saved. It shows at the top of every invoice.") : r.Error;
        return RedirectToPage();
    }

    [BindProperty]
    public InvoiceSettingsInput Input { get; set; } = new();

    public PortfolioSummary? Portfolio { get; private set; }

    public async Task OnGetAsync()
    {
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is not null)
        {
            Input = InvoiceSettingsInput.From(await invoices.GetSettingsAsync(Portfolio.PortfolioId));
            HasLogo = await extras.GetLogoAsync(Portfolio.PortfolioId) is not null;
        }
    }

    public async Task<IActionResult> OnPostAsync()
    {
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is null)
            return Forbid();
        foreach (var error in Input.Validate())
            ModelState.AddModelError(string.Empty, error);
        if (ModelState.ErrorCount > 0)
            return Page();

        var result = await invoices.SaveSettingsAsync(Portfolio.PortfolioId, Input);
        if (!result.Ok)
        {
            ModelState.AddModelError(string.Empty, result.Error!);
            return Page();
        }
        TempData["Message"] = "Invoice settings saved.";
        return RedirectToPage();
    }
}
