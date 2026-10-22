using System.Security.Claims;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Core.Regions;

namespace PortfolioManager.Web.Services;

/// <summary>
/// The signed-in user and the portfolio they are looking at, worked out once per request.
/// The choice is kept in a cookie but is always checked against the portfolios the user
/// may open, so editing the cookie can't reveal someone else's portfolio.
/// </summary>
public sealed class CurrentPortfolio(IHttpContextAccessor http, PortfolioService portfolios)
{
    public const string CookieName = "pm_portfolio";

    private bool _loaded;
    private IReadOnlyList<PortfolioSummary> _accessible = [];
    private PortfolioSummary? _selected;
    private RegionSettings? _region;

    public static int UserId(ClaimsPrincipal user) =>
        int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new InvalidOperationException("Not signed in"));

    public async Task<IReadOnlyList<PortfolioSummary>> GetAccessibleAsync()
    {
        await LoadAsync();
        return _accessible;
    }

    /// <summary>The portfolio to show, or null when the user can open none.</summary>
    public async Task<PortfolioSummary?> GetSelectedAsync()
    {
        await LoadAsync();
        return _selected;
    }

    /// <summary>The picked portfolio's country, year start, currency and format; Australia's when there is none.</summary>
    public async Task<RegionSettings> GetRegionAsync()
    {
        if (_region is null)
        {
            var selected = await GetSelectedAsync();
            _region = selected is null ? RegionSettings.Australia : await portfolios.GetRegionAsync(selected.PortfolioId);
        }
        return _region;
    }

    /// <summary>Switches portfolio. Returns false (and changes nothing) if the user may not open it.</summary>
    public async Task<bool> TrySelectAsync(int portfolioId)
    {
        await LoadAsync();
        var match = _accessible.FirstOrDefault(p => p.PortfolioId == portfolioId);
        if (match is null)
            return false;

        _selected = match;
        _region = null;
        Context.Response.Cookies.Append(CookieName, portfolioId.ToString(), new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            IsEssential = true,
            Expires = DateTimeOffset.UtcNow.AddYears(1),
        });
        return true;
    }

    /// <summary>Reloads the list on next use, e.g. after a portfolio is created.</summary>
    public void Refresh()
    {
        _loaded = false;
        _region = null;
    }

    private HttpContext Context => http.HttpContext ?? throw new InvalidOperationException("No current request");

    private async Task LoadAsync()
    {
        if (_loaded)
            return;
        var userId = UserId(Context.User);
        _accessible = await portfolios.GetAccessibleAsync(userId);
        int? requested = int.TryParse(Context.Request.Cookies[CookieName], out var id) ? id : null;
        _selected = PortfolioService.Choose(_accessible, userId, requested);
        _loaded = true;
    }
}
