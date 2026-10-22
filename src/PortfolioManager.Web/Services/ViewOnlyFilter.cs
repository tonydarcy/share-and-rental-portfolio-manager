using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace PortfolioManager.Web.Services;

/// <summary>Marks a page whose form posts don't change the picked portfolio (logging in, switching portfolio).</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class AllowViewOnlyAttribute : Attribute;

/// <summary>
/// Someone with View access to a portfolio can open every page but can't save anything to it:
/// any form post is turned away here, in one place, unless the page is marked [AllowViewOnly].
/// </summary>
public sealed class ViewOnlyFilter(CurrentPortfolio current, ITempDataDictionaryFactory tempData) : IAsyncPageFilter
{
    public const string Message = "You can look at this portfolio but not change it. Ask its owner for full access.";

    public Task OnPageHandlerSelectionAsync(PageHandlerSelectedContext context) => Task.CompletedTask;

    public async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        var http = context.HttpContext;
        if (Blocks(http.Request.Method, http.User.Identity?.IsAuthenticated == true, context.HandlerInstance.GetType())
            && await current.GetSelectedAsync() is { CanEdit: false })
        {
            tempData.GetTempData(http)["Error"] = Message;
            context.Result = new RedirectResult(http.Request.PathBase + http.Request.Path + http.Request.QueryString);
            return;
        }
        await next();
    }

    /// <summary>Whether a request needs edit access: a signed-in form post to a page not marked [AllowViewOnly].</summary>
    public static bool Blocks(string method, bool signedIn, Type pageModel) =>
        signedIn
        && !HttpMethods.IsGet(method) && !HttpMethods.IsHead(method) && !HttpMethods.IsOptions(method)
        && !pageModel.IsDefined(typeof(AllowViewOnlyAttribute), inherit: true);
}
