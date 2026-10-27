using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Exports;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Core.Tax;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Accounting;

/// <summary>Download the ledger as a Xero manual journal or MYOB general journal import file, with each account's code in that system.</summary>
public class ExportModel(CurrentPortfolio current, LedgerExportService export, ChartOfAccountsService chart, AccountingLinkService link,
    AccountingLinkOptions linkOptions, AccountingTokenProtector protector, SiteBranding site) : PageModel
{
    [BindProperty(SupportsGet = true)] public string System { get; set; } = ExportSystems.Xero;
    [BindProperty(SupportsGet = true)] public string Shape { get; set; } = ExportShapes.Journals;
    [BindProperty(SupportsGet = true)] public DateOnly? From { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? To { get; set; }

    [BindProperty] public List<MapRow> Map { get; set; } = [];

    public sealed class MapRow
    {
        public int AccountId { get; set; }
        public string? Code { get; set; }
        public string? Tax { get; set; }
    }

    public PortfolioSummary? Portfolio { get; private set; }
    public IReadOnlyList<LedgerAccount> Accounts { get; private set; } = [];
    public IReadOnlyDictionary<int, ExportMapping> Current { get; private set; } = new Dictionary<int, ExportMapping>();

    /// <summary>The host has registered the site with this system, so a portfolio can connect and send directly.</summary>
    public bool CanConnect => linkOptions.IsConfigured(System);
    public AccountingConnection? Connection { get; private set; }
    public IReadOnlyList<AccountingSend> Sends { get; private set; } = [];

    /// <summary>The address Xero or MYOB sends the sign-in back to; it must match the one registered with them.</summary>
    public static string RedirectUri(HttpRequest request, SiteBranding site) =>
        (site.BaseUrl is { Length: > 0 } b ? b.TrimEnd('/') : $"{request.Scheme}://{request.Host}{request.PathBase}") + "/Accounting/Connected";

    public async Task OnGetAsync()
    {
        if (await LoadAsync() is null)
            return;
        Accounts = (await chart.GetAccountsAsync(Portfolio!.PortfolioId)).Where(a => a.Postable != false).OrderBy(a => a.Number).ToList();
        Current = await export.GetMapAsync(Portfolio.PortfolioId, System);
        Connection = await link.GetAsync(Portfolio.PortfolioId, System);
        Sends = await link.GetSendsAsync(Portfolio.PortfolioId, System);
    }

    /// <summary>Starts the sign-in at Xero or MYOB; the answer comes back to /Accounting/Connected.</summary>
    public async Task<IActionResult> OnPostConnectAsync()
    {
        if (await LoadAsync() is not { } p)
            return Forbid();
        if (!CanConnect)
        {
            TempData["Error"] = $"This site hasn't been registered with {System} yet, so use the import file.";
            return RedirectToPage(new { System });
        }
        var state = protector.State(p.PortfolioId, CurrentPortfolio.UserId(User), System);
        return Redirect(AccountingApi.AuthorizeUrl(System, linkOptions.For(System), RedirectUri(Request, site), state));
    }

    public async Task<IActionResult> OnPostDisconnectAsync()
    {
        if (await LoadAsync() is not { } p)
            return Forbid();
        var r = await link.DisconnectAsync(p.PortfolioId, System);
        TempData[r.Ok ? "Message" : "Error"] = r.Ok ? $"Disconnected from {System}. Journals already sent stay there." : r.Error;
        return RedirectToPage(new { System });
    }

    /// <summary>Sends the same journals the download would hold. A second send of overlapping dates needs <paramref name="again"/>.</summary>
    public async Task<IActionResult> OnPostSendAsync(bool again)
    {
        if (await LoadAsync() is not { } p)
            return Forbid();
        var back = RedirectToPage(new { System, Shape, From = From?.ToString("yyyy-MM-dd"), To = To?.ToString("yyyy-MM-dd") });
        var from = Shape == ExportShapes.Balances ? null : From;
        if (!again && (await link.GetSendsAsync(p.PortfolioId, System)).FirstOrDefault(s => s.Sent > 0 && s.Overlaps(from, To!.Value)) is { } earlier)
        {
            TempData["Error"] = $"Journals for some of these dates were sent to {System} on {earlier.SentAt:d}. Sending again would put them in twice; " +
                                "tick Send again anyway if you deleted them there.";
            return back;
        }
        var lines = await export.GetLinesAsync(p.PortfolioId, from, To!.Value);
        var journals = LedgerExportFiles.Build(lines, Shape, System, To.Value, await export.GetMapAsync(p.PortfolioId, System));
        if (journals.Count == 0)
        {
            TempData["Error"] = "There's nothing in the ledger for those dates.";
            return back;
        }
        var outcome = await link.SendAsync(p.PortfolioId, CurrentPortfolio.UserId(User), System, Shape, from, To.Value, journals);
        if (outcome.Ok)
            TempData["Message"] = $"Sent {outcome.Sent} journal{(outcome.Sent == 1 ? "" : "s")} to {System}" +
                                  (System == ExportSystems.Xero ? " as drafts. Check and post them in Xero." : ".");
        else
            TempData["Error"] = (outcome.Sent > 0 ? $"{outcome.Sent} of {journals.Count} journals went. " : "") + string.Join(" ", outcome.Errors);
        return back;
    }

    public async Task<IActionResult> OnGetDownloadAsync()
    {
        if (await LoadAsync() is not { } p)
            return Forbid();
        var lines = await export.GetLinesAsync(p.PortfolioId, Shape == ExportShapes.Balances ? null : From, To!.Value);
        var journals = LedgerExportFiles.Build(lines, Shape, System, To.Value, await export.GetMapAsync(p.PortfolioId, System));
        if (journals.Count == 0)
        {
            TempData["Error"] = "There's nothing in the ledger for those dates.";
            return RedirectToPage(new { System, Shape, From = From?.ToString("yyyy-MM-dd"), To = To?.ToString("yyyy-MM-dd") });
        }
        var name = $"{System.ToLowerInvariant()}-{Shape.ToLowerInvariant()}-{To:yyyy-MM-dd}";
        return System == ExportSystems.Myob
            ? File(LedgerExportFiles.Myob(journals), "text/plain", name + ".txt")
            : File(LedgerExportFiles.Xero(journals), "text/csv", name + ".csv");
    }

    public async Task<IActionResult> OnPostMapAsync()
    {
        if (await LoadAsync() is not { } p)
            return Forbid();
        var old = await export.GetMapAsync(p.PortfolioId, System);
        var errors = new List<string>();
        var saved = 0;
        foreach (var row in Map)
        {
            var code = row.Code?.Trim() ?? "";
            var tax = row.Tax?.Trim() is { Length: > 0 } t ? t : null;
            var was = old.GetValueOrDefault(row.AccountId);
            if ((was?.Code ?? "") == code && was?.TaxCode == tax)
                continue;
            var r = await export.SaveMapAsync(p.PortfolioId, System, row.AccountId, code, tax);
            if (r.Ok) saved++; else errors.Add(r.Error!);
        }
        TempData["Message"] = saved == 0 ? "Nothing changed." : $"Saved {saved} account code{(saved == 1 ? "" : "s")} for {System}.";
        if (errors.Count > 0)
            TempData["Error"] = string.Join(" ", errors.Distinct());
        return RedirectToPage(new { System, Shape, From = From?.ToString("yyyy-MM-dd"), To = To?.ToString("yyyy-MM-dd") });
    }

    private async Task<PortfolioSummary?> LoadAsync()
    {
        if (!ExportSystems.All.Contains(System))
            System = ExportSystems.Xero;
        if (Shape is not (ExportShapes.Journals or ExportShapes.Monthly or ExportShapes.Balances))
            Shape = ExportShapes.Journals;
        var year = FinancialYear.EndYearFor(DateOnly.FromDateTime(DateTime.Today));
        From ??= FinancialYear.StartDate(year);
        To ??= FinancialYear.EndDate(year);
        Portfolio = await current.GetSelectedAsync();
        return Portfolio;
    }
}
