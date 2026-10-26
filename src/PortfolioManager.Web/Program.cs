using System.Globalization;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using PortfolioManager.Core.Alerts;
using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Accounts;
using PortfolioManager.Core.Api;
using PortfolioManager.Core.Cash;
using PortfolioManager.Core.Clients;
using PortfolioManager.Core.CorporateActions;
using PortfolioManager.Core.Dashboard;
using PortfolioManager.Core.Email;
using PortfolioManager.Core.Data;
using PortfolioManager.Core.Groups;
using PortfolioManager.Core.Holdings;
using PortfolioManager.Core.Import;
using PortfolioManager.Core.Invoicing;
using PortfolioManager.Core.Imports;
using PortfolioManager.Core.NavexaSync;
using PortfolioManager.Core.Payouts;
using PortfolioManager.Core.Performance;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Core.Prices;
using PortfolioManager.Core.Regions;
using PortfolioManager.Core.SalesTax;
using PortfolioManager.Core.Tax;
using PortfolioManager.Core.TaxWork;
using PortfolioManager.Core.Trades;
using PortfolioManager.Core.Reports;
using PortfolioManager.Core.Statements;
using PortfolioManager.Web.Api;
using PortfolioManager.Web.Services;
using System.Threading.RateLimiting;

// Australian dates and currency by default, whatever the server's own regional settings are;
// each signed-in request switches to the picked portfolio's settings below
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = new CultureInfo("en-AU");

var builder = WebApplication.CreateBuilder(args);

// The connection string never lives in git. Set it with `dotnet user-secrets` locally,
// or the ConnectionStrings__Default environment variable / appsettings.Production.json on the server.
var connectionString = builder.Configuration.GetConnectionString("Default");
if (string.IsNullOrWhiteSpace(connectionString))
    throw new InvalidOperationException("ConnectionStrings:Default is not set. See SETUP.md.");

// Each connection carries the signed-in login, for the audit trail
builder.Services.AddSingleton<IDbConnectionFactory>(sp => new AuditedConnectionFactory(connectionString, sp.GetRequiredService<IHttpContextAccessor>()));
builder.Services.AddScoped<AccountService>();
builder.Services.AddScoped<PortfolioService>();
builder.Services.AddScoped<DashboardService>();
builder.Services.AddScoped<HoldingsService>();
builder.Services.AddScoped<TradesService>();
builder.Services.AddScoped<TradeService>();
builder.Services.AddScoped<CashService>();
builder.Services.AddScoped<CapitalGainsService>();
builder.Services.AddScoped<CgtDiscountService>();
builder.Services.AddScoped<ParcelChoiceService>();
builder.Services.AddScoped<WhatIfService>();
builder.Services.AddScoped<TaxLossService>();
builder.Services.AddScoped<ConsolidatedService>();
builder.Services.AddScoped<LookThroughService>();
builder.Services.AddScoped<FundTaxStatementService>();
builder.Services.AddScoped<EssStatementService>();
builder.Services.AddScoped<EssGrantService>();
builder.Services.AddScoped<TradingStockService>();
builder.Services.AddScoped<PortfolioManager.Core.Debt.LoanService>();
builder.Services.AddScoped<PortfolioManager.Core.Debt.FixedIncomeService>();
builder.Services.AddScoped<CorporateActionsService>();
builder.Services.AddScoped<ParcelTransferService>();
builder.Services.AddScoped<AnnouncedActionsService>();
builder.Services.AddScoped<UnrealisedGainsService>();
builder.Services.AddScoped<TaxableIncomeService>();
builder.Services.AddScoped<NavexaImportService>();
builder.Services.AddHttpClient<NavexaApiClient>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Navexa:BaseUrl"] ?? NavexaApiClient.DefaultBaseAddress.ToString());
    client.Timeout = TimeSpan.FromSeconds(100);
});
builder.Services.AddSingleton<INavexaKeyProtector, NavexaKeyProtector>();
builder.Services.AddScoped<NavexaSyncService>();
builder.Services.AddHostedService<NavexaSyncScheduler>();
builder.Services.AddScoped<AlertService>();
builder.Services.AddHostedService<AlertScheduler>();
builder.Services.AddScoped<PayoutService>();
builder.Services.AddScoped<DiversityService>();
builder.Services.AddScoped<IncomeCalendarService>();
builder.Services.AddScoped<PerformanceService>();
builder.Services.AddScoped<QuoteService>();
builder.Services.AddScoped<FxRateService>();
builder.Services.AddScoped<ManualPriceService>();
builder.Services.AddScoped<GroupService>();
builder.Services.AddScoped<ImportService>();
builder.Services.AddScoped<ChartOfAccountsService>();
builder.Services.AddScoped<JournalService>();
builder.Services.AddScoped<LedgerReportService>();
builder.Services.AddScoped<RealisedGainService>();
builder.Services.AddScoped<BankImportService>();
builder.Services.AddScoped<RevaluationService>();
builder.Services.AddScoped<BankReconciliationService>();
builder.Services.AddScoped<LockedPeriodService>();
builder.Services.AddScoped<AllocationRuleService>();
builder.Services.AddScoped<ClientService>();
builder.Services.AddScoped<FinancialStatementsService>();
builder.Services.AddScoped<TaxYearService>();
builder.Services.AddScoped<TaxWorkpaperService>();
builder.Services.AddScoped<TrustBeneficiaryService>();
builder.Services.AddScoped<SmsfMemberService>();
builder.Services.AddScoped<FrankingAccountService>();
builder.Services.AddScoped<SalesTaxService>();
builder.Services.AddScoped<InvoiceService>();
builder.Services.AddScoped<RecurringJournalService>();
builder.Services.AddScoped<PortfolioManager.Core.Practice.AttachmentService>();
builder.Services.AddScoped<PortfolioManager.Core.Practice.AuditTrailService>();
builder.Services.AddScoped<PortfolioManager.Core.Practice.ClientQueryService>();
builder.Services.AddScoped<PortfolioManager.Core.Practice.YearEndService>();
builder.Services.AddScoped<PortfolioManager.Core.Practice.PracticeDashboardService>();
builder.Services.AddScoped<PortfolioManager.Core.Practice.OnboardingService>();
builder.Services.AddScoped<PortfolioManager.Core.Invoicing.InvoiceExtrasService>();
builder.Services.AddScoped<PortfolioManager.Core.Exports.LedgerExportService>();
builder.Services.AddScoped<PortfolioManager.Core.TaxWork.RentalService>();
builder.Services.AddScoped<PortfolioManager.Core.Rentals.PropertyService>();
builder.Services.AddScoped<PortfolioManager.Core.TaxWork.SmsfExtrasService>();
builder.Services.AddScoped<PortfolioManager.Core.TaxWork.PrefillService>();
builder.Services.AddScoped<PortfolioManager.Core.Bills.BillService>();
builder.Services.AddScoped<PortfolioManager.Core.Orders.QuoteOrderService>();
builder.Services.AddScoped<PortfolioManager.Core.Inventory.InventoryService>();
builder.Services.AddScoped<PortfolioManager.Core.Claims.ExpenseClaimService>();
builder.Services.AddScoped<PortfolioManager.Core.Assets.FixedAssetService>();
builder.Services.AddScoped<TrackingService>();
builder.Services.AddScoped<BudgetService>();
builder.Services.AddScoped<ForeignCurrencyService>();
builder.Services.AddSingleton(new PortfolioManager.Core.Reading.DocumentReadingOptions(
    builder.Configuration["DocumentReading:AnthropicApiKey"],
    builder.Configuration["DocumentReading:Model"] is { Length: > 0 } readingModel ? readingModel : "claude-opus-5-5"));
builder.Services.AddScoped<PortfolioManager.Core.Reading.DocumentReadingService>();
builder.Services.AddScoped<DocumentLists>();
//@@MORE
builder.Services.AddScoped<ApiKeyService>();
builder.Services.AddScoped<ApiAuditService>();
builder.Services.AddSingleton<AgentTools>();
builder.Services.AddScoped<AgentToolRunner>();
builder.Services.AddScoped<PortfolioManager.Core.Assistant.AssistantService>();
builder.Services.AddScoped<AssistantChat>();
builder.Services.AddScoped<McpServer>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<CurrentPortfolio>();
// Only these logins may upload market prices, which every portfolio shares (as on the legacy site)
// Site name, logo and links, so others can run their own copy under their own name
var site = builder.Configuration.GetSection("Site").Get<SiteBranding>() ?? new SiteBranding();
builder.Services.AddSingleton(site);

// Email (password resets, email checks, invitations, invoices) goes through the SMTP server in the Email section;
// without one nothing is sent and pages offer another way
var emailSettings = builder.Configuration.GetSection("Email").Get<EmailSettings>() ?? new EmailSettings();
builder.Services.AddSingleton(emailSettings);
if (emailSettings.IsConfigured)
    builder.Services.AddSingleton<IEmailSender>(new SmtpEmailSender(emailSettings, site.Name));
else
    builder.Services.AddSingleton<IEmailSender, LogOnlyEmailSender>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<AccountTokens>();
builder.Services.AddScoped<SignInFlow>();
builder.Services.AddSingleton(builder.Configuration.GetSection("Captcha").Get<CaptchaSettings>() ?? new CaptchaSettings());
builder.Services.AddHttpClient<Captcha>(client => client.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddSingleton(new SiteAdmin(builder.Configuration.GetSection("SiteAdminUserIds").Get<int[]>() ?? []));

// Sign-in cookies are encrypted with keys that must survive app restarts,
// otherwise everyone is logged out whenever IIS recycles the app pool.
var keysPath = builder.Configuration["DataProtection:KeysPath"];
if (!string.IsNullOrWhiteSpace(keysPath))
    builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keysPath));

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Account/Login";
        options.LogoutPath = "/Account/Logout";
        options.Cookie.Name = "pm_auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.ExpireTimeSpan = TimeSpan.FromDays(14);
        options.SlidingExpiration = true;
    })
    // The JSON API and MCP server sign in with an API key instead (Account > API keys)
    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, null);

// Every page needs a signed-in user unless it opts out with [AllowAnonymous]
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
    .AddPolicy(ApiEndpoints.Policy, ApiEndpoints.BuildPolicy());

// 120 API or MCP requests a minute from each address, so a runaway agent (or someone guessing keys) can't swamp the server
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = (context, ct) => new ValueTask(context.HttpContext.Response.WriteAsync(
        "Too many attempts from your address. Wait a minute, then go back and try again.", ct));
    options.AddPolicy(ApiEndpoints.RateLimit, context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1) }));
    // 10 form posts a minute from each address to the login, sign-up and email pages, to slow password guessing
    // and stop the site being used to send floods of email. Opening the pages isn't limited.
    options.AddPolicy(SignInFlow.RateLimit, context => HttpMethods.IsGet(context.Request.Method)
        ? RateLimitPartition.GetNoLimiter("")
        : RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
});

builder.Services.AddScoped<ViewOnlyFilter>();
builder.Services.AddScoped<LockedPeriodFilter>();
builder.Services.AddRazorPages(options =>
{
    // People with View access to a portfolio can't post changes to it
    options.Conventions.ConfigureFilter(new ServiceFilterAttribute(typeof(ViewOnlyFilter)));
    // A change the database refuses because the year is locked shows its message on the page
    options.Conventions.ConfigureFilter(new ServiceFilterAttribute(typeof(LockedPeriodFilter)));
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();
// Before sign-in, so API key guesses are limited before they reach the database
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// Dates, numbers, currency and financial years follow the picked portfolio's settings (Portfolio settings page)
app.Use(async (context, next) =>
{
    if (context.User.Identity?.IsAuthenticated == true
        && context.GetEndpoint()?.Metadata.GetMetadata<Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor>() is not null)
    {
        var region = await context.RequestServices.GetRequiredService<CurrentPortfolio>().GetRegionAsync();
        RegionSettings.Current = region;
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = region.Culture();
    }
    await next(context);
});

app.MapAgentApi();
// Styles, scripts and icons are public: the login page and the home-screen app need them before sign-in
app.MapStaticAssets().AllowAnonymous();
// Lets phones add the site to the home screen as an app (wwwroot/sw.js), under the site's own name
app.MapGet("/manifest.webmanifest", (SiteBranding branding) => Results.Json(WebManifest.For(branding.Name), contentType: "application/manifest+json"))
   .AllowAnonymous();
app.MapRazorPages()
   .WithStaticAssets();

app.Run();
