using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using PortfolioManager.Core.Accounting;

namespace PortfolioManager.Web.Services;

/// <summary>
/// When the database refuses a form post because it would change a locked period, the page is shown
/// again with the database's message instead of the error page. Only posts are handled, so a page
/// that fails to load can't redirect to itself for ever.
/// </summary>
public sealed class LockedPeriodFilter(ITempDataDictionaryFactory tempData) : IAsyncPageFilter
{
    public Task OnPageHandlerSelectionAsync(PageHandlerSelectedContext context) => Task.CompletedTask;

    public async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        var executed = await next();
        var http = context.HttpContext;
        if (executed.Exception is { } ex && !executed.ExceptionHandled && HttpMethods.IsPost(http.Request.Method)
            && LockedPeriodService.IsLockError(ex))
        {
            tempData.GetTempData(http)["Error"] = Message(ex);
            executed.Result = new RedirectResult(http.Request.PathBase + http.Request.Path + http.Request.QueryString);
            executed.ExceptionHandled = true;
        }
    }

    public static string Message(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
            if (e is Microsoft.Data.SqlClient.SqlException { Number: LockedPeriodService.ErrorNumber } sql)
                return sql.Message;
        return ex.Message;
    }
}
