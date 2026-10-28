using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace PortfolioManager.Web.Services;

/// <summary>The "Security" section of appsettings.</summary>
public sealed class SecuritySettings
{
    /// <summary>Site admins (SiteAdminUserIds) must turn on two-factor sign-in before they can use the site. On unless set to false.</summary>
    public bool RequireTwoFactorForAdmins { get; set; } = true;
}

/// <summary>
/// A site admin can see and change things for every user, so until they turn on two-factor sign-in every page
/// sends them to Account > Two-factor sign-in (the account pages and logging out still work).
/// </summary>
public sealed class AdminTwoFactorFilter(SecuritySettings settings, SiteAdmin admin, ITempDataDictionaryFactory tempData) : IAsyncPageFilter
{
    public const string Message = "You're a site admin, so please turn on two-factor sign-in before carrying on.";

    public Task OnPageHandlerSelectionAsync(PageHandlerSelectedContext context) => Task.CompletedTask;

    public async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        var http = context.HttpContext;
        if (settings.RequireTwoFactorForAdmins && admin.IsAdmin(http.User) && !AccountSecurity.HasTwoFactor(http.User)
            && !IsAccountPage(context.ActionDescriptor.ViewEnginePath))
        {
            tempData.GetTempData(http)["Error"] = Message;
            context.Result = new RedirectToPageResult("/Account/TwoFactor");
            return;
        }
        await next();
    }

    /// <summary>Pages an admin without two-factor sign-in can still open: everything under Account, and the error page.</summary>
    public static bool IsAccountPage(string? page) =>
        page is not null && (page.StartsWith("/Account/", StringComparison.OrdinalIgnoreCase) || page == "/Error");
}
