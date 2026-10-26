using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Exports;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Accounting;

/// <summary>
/// Where Xero and MYOB send the sign-in back to (?code=..&amp;state=.., and &amp;businessId=.. from MYOB). The state is
/// signed by this site, so the connection is only saved for the portfolio and login that started it.
/// </summary>
public class ConnectedModel(CurrentPortfolio current, AccountingLinkService link, AccountingTokenProtector protector, SiteBranding site) : PageModel
{
    public async Task<IActionResult> OnGetAsync(string? code, string? state, string? businessId, string? error)
    {
        var started = protector.ReadState(state);
        var system = started?.System ?? ExportSystems.Xero;
        IActionResult Back(string key, string message)
        {
            TempData[key] = message;
            return RedirectToPage("/Accounting/Export", new { System = system });
        }

        if (started is null)
            return Back("Error", "That sign-in had expired or didn't start here. Connect again from this page.");
        if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(code))
            return Back("Error", $"{system} didn't connect{(error == "access_denied" ? " because access wasn't allowed" : "")}. Nothing was changed.");
        if (await current.GetSelectedAsync() is not { CanEdit: true } p || p.PortfolioId != started.Value.PortfolioId
            || CurrentPortfolio.UserId(User) != started.Value.LoginUserId)
            return Back("Error", "The portfolio was changed during the sign-in, so nothing was saved. Pick the portfolio and connect again.");

        var r = await link.ConnectAsync(p.PortfolioId, started.Value.LoginUserId, system, code, ExportModel.RedirectUri(Request, site), businessId);
        return r.Ok
            ? Back("Message", $"Connected {p.Name} to {system}. Journals can now be sent straight there.")
            : Back("Error", r.Error ?? $"{system} didn't connect.");
    }
}
