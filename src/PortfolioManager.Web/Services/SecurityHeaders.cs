namespace PortfolioManager.Web.Services;

/// <summary>
/// Browser security headers sent with every response: what the page may load, that it can't be shown inside
/// another site's frame (clickjacking), and how much of the address is passed on when a link leaves the site.
/// </summary>
public static class SecurityHeaders
{
    // Bootstrap, its icons, Chart.js and the QR code library come from jsDelivr (pinned with integrity hashes);
    // the "I'm human" check comes from Cloudflare.
    private const string Cdn = "https://cdn.jsdelivr.net";
    private const string Turnstile = "https://challenges.cloudflare.com";

    /// <summary>
    /// The Content-Security-Policy. Inline scripts and styles stay allowed because many pages use onclick and
    /// style attributes; the policy still blocks scripts from anywhere else, plugins, framing and base tag tricks.
    /// The OAuth consent page sends people back to the app that asked (any https address, or this computer),
    /// so form posts there may end up off the site.
    /// </summary>
    public static string ContentSecurityPolicy(bool oauthConsent) => string.Join("; ",
        "default-src 'self'",
        $"script-src 'self' 'unsafe-inline' {Cdn} {Turnstile}",
        $"style-src 'self' 'unsafe-inline' {Cdn}",
        $"font-src 'self' {Cdn}",
        // A logo can be a full https address (Site:LogoUrl)
        "img-src 'self' data: https:",
        "connect-src 'self'",
        $"frame-src {Turnstile}",
        "worker-src 'self'",
        "manifest-src 'self'",
        "object-src 'none'",
        "base-uri 'self'",
        oauthConsent ? "form-action 'self' https: http://localhost:* http://127.0.0.1:* http://[::1]:*" : "form-action 'self'",
        "frame-ancestors 'none'");

    public const string PermissionsPolicy = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";

    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers.ContentSecurityPolicy = ContentSecurityPolicy(
                context.Request.Path.StartsWithSegments("/Account/Connect", StringComparison.OrdinalIgnoreCase));
            headers.XFrameOptions = "DENY";
            headers.XContentTypeOptions = "nosniff";
            headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            headers["Permissions-Policy"] = PermissionsPolicy;
            return Task.CompletedTask;
        });
        await next(context);
    });
}
