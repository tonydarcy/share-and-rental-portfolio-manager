namespace PortfolioManager.Web.Services;

/// <summary>The web app manifest that lets a phone install the site on its home screen.</summary>
public static class WebManifest
{
    public static object For(string siteName)
    {
        var shortName = siteName.Length <= 12 ? siteName : string.Concat(siteName.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => w[0]));
        return new Dictionary<string, object>
        {
            ["name"] = siteName,
            ["short_name"] = shortName,
            ["start_url"] = "/",
            ["scope"] = "/",
            ["display"] = "standalone",
            ["background_color"] = "#f8f9fa",
            ["theme_color"] = "#212529",
            ["icons"] = new[]
            {
                new Dictionary<string, string> { ["src"] = "/icons/icon-192.png", ["sizes"] = "192x192", ["type"] = "image/png" },
                new Dictionary<string, string> { ["src"] = "/icons/icon-512.png", ["sizes"] = "512x512", ["type"] = "image/png", ["purpose"] = "any maskable" },
            },
        };
    }
}
