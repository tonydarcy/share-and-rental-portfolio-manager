using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Caching.Memory;
using PortfolioManager.Core.Accounts;

namespace PortfolioManager.Web.Services;

/// <summary>
/// The "Legal" section of appsettings: the terms of use, privacy policy and disclaimer for a hosted site.
/// On by default. Someone running a copy only for themselves can set Enabled to false; the source code link stays.
/// </summary>
public sealed class LegalSettings
{
    /// <summary>The version of the built-in terms and privacy policy. Changing it asks everyone to agree again.</summary>
    public const string BuiltInVersion = "2026-10-28";

    public const string DefaultSourceCodeUrl = "https://github.com/tonydarcy/share-and-rental-portfolio-manager";

    /// <summary>Shows the Terms, Privacy and Disclaimer pages and links, the sign-up checkbox and the notes on tax pages.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Signed-in people who haven't agreed to the current terms are asked to before they carry on.</summary>
    public bool RequireAcceptance { get; set; } = true;

    /// <summary>Who runs this copy of the site ("we" in the terms), e.g. "Example Pty Ltd". Empty uses the site name.</summary>
    public string? OperatorName { get; set; }

    /// <summary>The operator's ABN (or other business number), shown in the terms and privacy policy. Hidden when empty.</summary>
    public string? Abn { get; set; }

    /// <summary>Where privacy and legal questions go. Empty uses Site:SupportEmail.</summary>
    public string? ContactEmail { get; set; }

    /// <summary>Where the site's server and database are, e.g. "in Sydney, Australia". Shown in the privacy policy; check it with your host.</summary>
    public string? HostingLocation { get; set; }

    /// <summary>The operator's own terms of use, used instead of the built-in page. Set TermsVersion when using it.</summary>
    public string? TermsUrl { get; set; }

    /// <summary>The operator's own privacy policy, used instead of the built-in page.</summary>
    public string? PrivacyUrl { get; set; }

    /// <summary>The version people agree to. Empty means the built-in terms' version. Change it when the terms change.</summary>
    public string? TermsVersion { get; set; }

    /// <summary>
    /// Where this copy's source code is. The AGPL-3.0 licence requires a site running a changed copy to offer its users
    /// the changed source, so point this at your own fork if you change the code. Always shown.
    /// </summary>
    public string SourceCodeUrl { get; set; } = DefaultSourceCodeUrl;

    public string Version => string.IsNullOrWhiteSpace(TermsVersion) ? BuiltInVersion : TermsVersion.Trim();

    public string Operator(SiteBranding site) => string.IsNullOrWhiteSpace(OperatorName) ? site.Name : OperatorName.Trim();

    public string? Contact(SiteBranding site) => string.IsNullOrWhiteSpace(ContactEmail) ? site.SupportEmail : ContactEmail.Trim();

    public bool AsksForAcceptance => Enabled && RequireAcceptance;

    /// <summary>
    /// Pages that show tax figures, BAS, CGT or anything else someone might lodge or rely on, which carry a short
    /// "check these figures" note. Matched on the page path (e.g. "/TaxWork/Rental").
    /// </summary>
    public static bool ShowsFiguresNote(string? page) =>
        page is not null && (page.StartsWith("/TaxWork/", StringComparison.OrdinalIgnoreCase)
            || page.StartsWith("/SalesTax/", StringComparison.OrdinalIgnoreCase)
            || FiguresPages.Contains(page));

    private static readonly HashSet<string> FiguresPages = new(StringComparer.OrdinalIgnoreCase)
    {
        "/Reports/CapitalGains", "/Reports/TaxableIncome", "/Reports/UnrealisedGains", "/Reports/TaxLoss",
        "/Reports/Consolidated", "/Reports/WhatIf",
    };

    public const string FiguresNote = "These figures are worked out from the records entered and may contain errors. "
        + "Check them, or have your own tax agent check them, before you lodge or rely on them. This is general information, not tax or financial advice.";

    /// <summary>The note on the assistant's page (Pages/Assistant/Index).</summary>
    public const string AssistantNote = "Answers come from an AI model and can be wrong, so check them before you rely on them. "
        + "Your questions and the records needed to answer them are sent to Anthropic in the United States.";

    public static bool ShowsAssistantNote(string? page) => string.Equals(page, "/Assistant/Index", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Pages a signed-in person can open before agreeing to the current terms: the legal pages themselves,
    /// the account pages (logging out, two-factor sign-in), and the error page.
    /// </summary>
    public static bool OpenBeforeAccepting(string? page) =>
        page is not null && (page.StartsWith("/Legal/", StringComparison.OrdinalIgnoreCase)
            || page.StartsWith("/Account/", StringComparison.OrdinalIgnoreCase) || page == "/Error");
}

/// <summary>
/// Sends a signed-in person who hasn't agreed to the current terms (Legal:TermsVersion) to Legal/Accept first.
/// The demo login is let through. What each login agreed to is cached for 10 minutes to save a database call per page.
/// </summary>
public sealed class TermsFilter(LegalSettings legal, SiteBranding site, TermsService terms, IMemoryCache cache) : IAsyncPageFilter
{
    public static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(10);

    public Task OnPageHandlerSelectionAsync(PageHandlerSelectedContext context) => Task.CompletedTask;

    public async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        var user = context.HttpContext.User;
        if (legal.AsksForAcceptance && user.Identity?.IsAuthenticated == true
            && !LegalSettings.OpenBeforeAccepting(context.ActionDescriptor.ViewEnginePath)
            && !(site.HasDemo && string.Equals(user.Identity.Name, site.DemoEmail, StringComparison.OrdinalIgnoreCase))
            && await AcceptedVersionAsync(CurrentPortfolio.UserId(user)) != legal.Version)
        {
            var back = context.HttpContext.Request.PathBase + context.HttpContext.Request.Path + context.HttpContext.Request.QueryString;
            context.Result = new RedirectToPageResult("/Legal/Accept", new { returnUrl = back.ToString() });
            return;
        }
        await next();
    }

    public async Task<string?> AcceptedVersionAsync(int userId) =>
        await cache.GetOrCreateAsync(CacheKey(userId), entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheFor;
            return terms.AcceptedVersionAsync(userId);
        });

    /// <summary>Records agreement to the current terms and remembers it straight away.</summary>
    public async Task AcceptAsync(int userId, string? ipAddress)
    {
        await terms.RecordAsync(userId, legal.Version, ipAddress);
        cache.Set(CacheKey(userId), (string?)legal.Version, CacheFor);
    }

    private static string CacheKey(int userId) => $"terms:{userId}";
}
