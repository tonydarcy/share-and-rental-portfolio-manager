using System.Security.Claims;
using Microsoft.Extensions.Caching.Memory;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Core.Regions;

namespace PortfolioManager.Web.Services;

/// <summary>
/// The signed-in user and the portfolio they are looking at, worked out once per request.
/// The choice is kept in a cookie but is always checked against the portfolios the user
/// may open, so editing the cookie can't reveal someone else's portfolio.
/// </summary>
public sealed class CurrentPortfolio(IHttpContextAccessor http, PortfolioService portfolios, IMemoryCache cache)
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

    /// <summary>
    /// What the top menu needs for this login and portfolio. Kept for two minutes, as every page draws the menu;
    /// saving the menu choices clears it (<see cref="ForgetMenu"/>).
    /// </summary>
    public async Task<MenuUsage> GetMenuUsageAsync()
    {
        var userId = UserId(Context.User);
        var selected = await GetSelectedAsync();
        var region = await GetRegionAsync();
        var count = (await GetAccessibleAsync()).Count;
        if (selected is null)
            return MenuUsage.Everything(count, region.IsAustralia);
        var usage = await cache.GetOrCreateAsync(MenuKey(userId, selected.PortfolioId), entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(2);
            return portfolios.GetMenuUsageAsync(userId, selected.PortfolioId, count, region.IsAustralia);
        });
        // The portfolio count and country can change between requests; the rest is what the cache saves
        return usage! with { PortfolioCount = count, IsAustralia = region.IsAustralia };
    }

    /// <summary>Makes the next page read the menu choices again, after they or the portfolio's records change.</summary>
    public void ForgetMenu(int portfolioId) => cache.Remove(MenuKey(UserId(Context.User), portfolioId));

    private static string MenuKey(int userId, int portfolioId) => $"menu:{userId}:{portfolioId}";

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
