using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Security;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Account;

/// <summary>The login's security log: sign-ins, wrong passwords and codes, and changes to passwords, two-factor and API keys.</summary>
[AllowViewOnly]
public class ActivityModel(SecurityLogService log) : PageModel
{
    public IReadOnlyList<SecurityEvent> Events { get; private set; } = [];

    public async Task OnGetAsync() => Events = await log.RecentAsync(CurrentPortfolio.UserId(User), 100);

    /// <summary>A short name for a browser's user agent, e.g. "Chrome on Windows".</summary>
    public static string Browser(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
            return "";
        var ua = userAgent;
        var browser = ua.Contains("Edg/") ? "Edge" : ua.Contains("OPR/") ? "Opera" : ua.Contains("Firefox/") ? "Firefox"
            : ua.Contains("Chrome/") ? "Chrome" : ua.Contains("Safari/") ? "Safari" : "";
        var system = ua.Contains("iPhone") || ua.Contains("iPad") ? "iPhone or iPad" : ua.Contains("Android") ? "Android"
            : ua.Contains("Windows") ? "Windows" : ua.Contains("Mac OS") ? "Mac" : ua.Contains("Linux") ? "Linux" : "";
        return (browser, system) switch
        {
            ("", "") => ua.Length > 60 ? ua[..60] + "…" : ua,
            (_, "") => browser,
            ("", _) => system,
            _ => $"{browser} on {system}",
        };
    }
}
