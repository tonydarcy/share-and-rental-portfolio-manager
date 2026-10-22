namespace PortfolioManager.Web.Services;

/// <summary>
/// The site's name, logo and links (the "Site" section of appsettings), so anyone can run their own copy
/// under their own name. Every setting is optional.
/// </summary>
public sealed class SiteBranding
{
    public const string DefaultName = "Share Portfolio Manager";

    /// <summary>Shown in the menu bar, page titles, emails and to AI assistants.</summary>
    public string Name { get; set; } = DefaultName;

    /// <summary>A small image shown before the name in the menu bar, e.g. "/img/logo.png" or a full https address.</summary>
    public string? LogoUrl { get; set; }

    /// <summary>The site's public address, e.g. "https://app.example.com", used for links in emails.
    /// Left empty, links use the address the request came in on.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>A link to an older site shown on the login page (e.g. while moving people across). Hidden when empty.</summary>
    public string? OldSiteUrl { get; set; }

    public string OldSiteLabel { get; set; } = "Go to the old site";

    /// <summary>Where people can ask for help, shown at the foot of every page and in emails. Hidden when empty.</summary>
    public string? SupportEmail { get; set; }

    /// <summary>Demo login shown on the login page (see Database/SampleData). Hidden when empty.</summary>
    public string? DemoEmail { get; set; }

    public string? DemoPassword { get; set; }

    public bool HasDemo => !string.IsNullOrWhiteSpace(DemoEmail) && !string.IsNullOrWhiteSpace(DemoPassword);

    /// <summary>An absolute link to a path on this site, for emails.</summary>
    public string Link(HttpRequest request, string pathAndQuery)
    {
        var root = string.IsNullOrWhiteSpace(BaseUrl) ? $"{request.Scheme}://{request.Host}{request.PathBase}" : BaseUrl;
        return root.TrimEnd('/') + "/" + pathAndQuery.TrimStart('/');
    }
}
