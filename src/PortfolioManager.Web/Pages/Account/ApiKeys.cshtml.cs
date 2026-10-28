using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Api;
using PortfolioManager.Core.Security;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Account;

/// <summary>
/// The signed-in login's API keys, for AI assistants (MCP) and other apps, and the log of what they changed.
/// A key acts as this login, so it reaches the same portfolios with the same role; a read-only key only looks.
/// </summary>
[AllowViewOnly]
public class ApiKeysModel(ApiKeyService keys, ApiAuditService audit, AccountSecurity security) : PageModel
{
    [BindProperty]
    public string? Name { get; set; }

    [BindProperty]
    public bool ReadOnly { get; set; }

    public IReadOnlyList<ApiKeyInfo> Keys { get; private set; } = [];
    public IReadOnlyList<ApiAuditEntry> Activity { get; private set; } = [];

    /// <summary>The key just made, shown once.</summary>
    public string? NewKey { get; private set; }

    public string? LoadError { get; private set; }

    public string BaseUrl => $"{Request.Scheme}://{Request.Host}{Request.PathBase}";

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostCreateAsync()
    {
        if (ApiKeyService.ValidateName(Name) is { } error)
        {
            ModelState.AddModelError(nameof(Name), error);
            await LoadAsync();
            return Page();
        }

        var made = await keys.CreateAsync(CurrentPortfolio.UserId(User), Name!, ReadOnly);
        if (!made.Result.Ok)
        {
            ModelState.AddModelError(nameof(Name), made.Result.Error!);
            await LoadAsync();
            return Page();
        }

        await security.AddAsync(HttpContext, CurrentPortfolio.UserId(User), User.Identity?.Name, SecurityEvents.ApiKeyCreated);
        // Shown on this response only: it isn't kept anywhere it could be shown again
        NewKey = made.Key;
        Name = null;
        ReadOnly = false;
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostRevokeAsync(int apiKeyId)
    {
        var result = await keys.RevokeAsync(CurrentPortfolio.UserId(User), apiKeyId);
        if (result.Ok)
            await security.AddAsync(HttpContext, CurrentPortfolio.UserId(User), User.Identity?.Name, SecurityEvents.ApiKeyRevoked);
        TempData[result.Ok ? "Message" : "Error"] = result.Ok ? "Key revoked. Anything using it stops working now." : result.Error;
        return RedirectToPage();
    }

    private async Task LoadAsync()
    {
        var userId = CurrentPortfolio.UserId(User);
        try
        {
            Keys = await keys.GetKeysAsync(userId);
            Activity = await audit.GetRecentAsync(userId, 100);
        }
        catch (Microsoft.Data.SqlClient.SqlException)
        {
            LoadError = "API keys need the 2026-10-20-api-keys.sql database migration. Ask whoever runs the site to run it.";
        }
    }
}
