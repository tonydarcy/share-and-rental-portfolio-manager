using System.Net;

namespace PortfolioManager.Web.Services;

/// <summary>
/// A short web page for error responses that would otherwise have no body or no type, such as a form that has
/// expired (400) or too many login attempts (429). Without a type, and with "nosniff" on (SecurityHeaders), Safari
/// on iPhone and iPad saves the response as a file instead of showing it.
/// </summary>
public static class PlainErrorPages
{
    public const string ContentType = "text/html; charset=utf-8";

    /// <summary>The JSON API, MCP server and OAuth endpoints answer apps, not people, so they keep their own replies.</summary>
    public static bool ForApps(PathString path) =>
        path.StartsWithSegments("/api") || path.StartsWithSegments("/mcp") || path.StartsWithSegments("/oauth")
        || path.StartsWithSegments("/.well-known");

    public static string Message(int status) => status switch
    {
        400 => "This form was open too long, or was sent twice. Go back, refresh the page and try again.",
        404 => "That page doesn't exist.",
        405 => "That address can't be used like that. Go back and try again.",
        429 => "Too many attempts from your address. Wait a minute, then go back and try again.",
        _ => "Something went wrong. Go back and try again.",
    };

    public static string Html(int status) => $$"""
        <!DOCTYPE html>
        <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
        <title>{{status}} {{WebUtility.HtmlEncode(ReasonPhrase(status))}}</title></head>
        <body style="font-family:system-ui,sans-serif;max-width:32rem;margin:3rem auto;padding:0 1rem;line-height:1.5">
        <h1 style="font-size:1.4rem">{{WebUtility.HtmlEncode(ReasonPhrase(status))}}</h1>
        <p>{{WebUtility.HtmlEncode(Message(status))}}</p>
        <p><a href="/">Go to the home page</a></p>
        </body></html>
        """;

    private static string ReasonPhrase(int status) =>
        Microsoft.AspNetCore.WebUtilities.ReasonPhrases.GetReasonPhrase(status) is { Length: > 0 } phrase ? phrase : "Error";

    /// <summary>Writes the page (or plain text for apps) with its type set.</summary>
    public static Task WriteAsync(HttpContext context, CancellationToken cancel = default)
    {
        var response = context.Response;
        var status = response.StatusCode;
        if (ForApps(context.Request.Path))
        {
            response.ContentType = "text/plain; charset=utf-8";
            return response.WriteAsync(Message(status), cancel);
        }
        response.ContentType = ContentType;
        response.Headers.CacheControl = "no-store";
        return response.WriteAsync(Html(status), cancel);
    }

    /// <summary>Gives every empty error response on the site's pages a page to show.</summary>
    public static IApplicationBuilder UsePlainErrorPages(this IApplicationBuilder app) =>
        app.UseStatusCodePages(context => ForApps(context.HttpContext.Request.Path)
            ? Task.CompletedTask
            : WriteAsync(context.HttpContext, context.HttpContext.RequestAborted));
}
