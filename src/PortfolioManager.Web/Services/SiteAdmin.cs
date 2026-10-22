using System.Security.Claims;

namespace PortfolioManager.Web.Services;

/// <summary>The site owner's logins (SiteAdminUserIds in appsettings), for features that affect every portfolio.</summary>
public sealed class SiteAdmin(IReadOnlyCollection<int> userIds)
{
    public bool IsAdmin(ClaimsPrincipal user) =>
        user.Identity?.IsAuthenticated == true && userIds.Contains(CurrentPortfolio.UserId(user));
}
