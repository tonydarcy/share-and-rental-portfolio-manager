using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Cash;
using PortfolioManager.Core.Import;
using PortfolioManager.Core.NavexaSync;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Import;

public class NavexaSyncModel(
    CurrentPortfolio current,
    NavexaSyncService sync,
    CashService cash,
    ChartOfAccountsService ledger,
    ILogger<NavexaSyncModel> logger) : PageModel
{
    public const int MaxRowsShown = 300;

    public PortfolioSummary? Portfolio { get; private set; }
    public NavexaConnection? Connection { get; private set; }
    public SyncPlan? Plan { get; private set; }
    public int ImportedCount { get; private set; }
    public IReadOnlyList<string> BankAccounts { get; private set; } = [];
    public IReadOnlyList<AccountChoice> LedgerAccounts { get; private set; } = [];
    public string AccountingMode { get; private set; } = "SingleEntry";
    public bool DoubleEntry => AccountingMode is "Hybrid" or "DoubleEntry";

    // Step 1 of connecting: the key, then the Navexa portfolio to copy
    [BindProperty]
    public string? ApiKey { get; set; }

    [BindProperty]
    public string? ProtectedKey { get; set; }

    [BindProperty]
    public int? NavexaPortfolioId { get; set; }

    /// <summary>Navexa's own portfolio id, typed in by hand. It is not this site's portfolio number.</summary>
    [BindProperty]
    public int? TypedNavexaPortfolioId { get; set; }

    public IReadOnlyList<NavexaApiPortfolio>? NavexaPortfolios { get; private set; }

    // Settings
    [BindProperty]
    public string? TradeBankAccount { get; set; }

    [BindProperty]
    public DateOnly? ImportFrom { get; set; }

    [BindProperty]
    public SyncSchedule Schedule { get; set; }

    // Mappings and answers
    [BindProperty]
    public List<HoldingRow> Holdings { get; set; } = [];

    [BindProperty]
    public List<CashAccountRow> CashAccounts { get; set; } = [];

    [BindProperty]
    public List<CashTypeRow> CashTypes { get; set; } = [];

    [BindProperty]
    public List<InputRow> Inputs { get; set; } = [];

    [TempData]
    public string? Done { get; set; }

    [TempData]
    public string? Problems { get; set; }

    public sealed class HoldingRow
    {
        public string Key { get; set; } = "";
        public string? Label { get; set; }
        public bool Skip { get; set; }
        public string? Exchange { get; set; }
        public string? Code { get; set; }
    }

    public sealed class CashAccountRow
    {
        public string Key { get; set; } = "";
        public string? Label { get; set; }
        public bool Skip { get; set; }
        public string? BankAccount { get; set; }
    }

    public sealed class CashTypeRow
    {
        public string Key { get; set; } = "";
        public string? Label { get; set; }
        /// <summary>Empty: Owner's Capital.</summary>
        public int? AccountId { get; set; }
    }

    public sealed class InputRow
    {
        public string Key { get; set; } = "";
        public decimal? FrankingCredit { get; set; }
        public int? DrpUnits { get; set; }
        public decimal? DrpPrice { get; set; }
        public int? SplitNewUnits { get; set; }
        public int? SplitOldUnits { get; set; }
        /// <summary>"add" (a different record) or "same" (already in the portfolio), for a possible duplicate.</summary>
        public string? Duplicate { get; set; }
    }

    public async Task OnGetAsync() => await LoadAsync(withPlan: true);

    public async Task<IActionResult> OnPostConnectAsync()
    {
        if (!await LoadAsync(withPlan: false))
            return Page();
        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            ModelState.AddModelError(nameof(ApiKey), "Paste your Navexa API key.");
            return Page();
        }
        if (TypedNavexaPortfolioId is { } typed)
            return await ConnectToAsync(ApiKey, typed, nameof(TypedNavexaPortfolioId));
        try
        {
            NavexaPortfolios = await sync.GetNavexaPortfoliosAsync(ApiKey, HttpContext.RequestAborted);
        }
        catch (NavexaApiException ex)
        {
            ModelState.AddModelError(nameof(ApiKey), ex.Message);
            return Page();
        }
        if (NavexaPortfolios.Count == 0)
        {
            ModelState.AddModelError(nameof(ApiKey), "That key works, but the Navexa account has no portfolios.");
            return Page();
        }
        // Carried to the next step encrypted, never as the plain key
        ProtectedKey = sync.ProtectKey(ApiKey);
        ApiKey = null;
        ModelState.Remove(nameof(ApiKey));
        ModelState.Remove(nameof(ProtectedKey));
        return Page();
    }

    public async Task<IActionResult> OnPostChooseAsync()
    {
        if (!await LoadAsync(withPlan: false))
            return Page();
        var key = ProtectedKey is null ? null : sync.UnprotectKey(ProtectedKey);
        if (key is null || (NavexaPortfolioId is null && TypedNavexaPortfolioId is null))
        {
            ModelState.AddModelError("", "Enter the API key again.");
            return Page();
        }
        if (TypedNavexaPortfolioId is { } typed)
            return await ConnectToAsync(key, typed, "");
        try
        {
            var portfolios = await sync.GetNavexaPortfoliosAsync(key, HttpContext.RequestAborted);
            var chosen = portfolios.FirstOrDefault(p => p.Id == NavexaPortfolioId);
            if (chosen is null)
            {
                ModelState.AddModelError("", "Choose one of the Navexa portfolios listed.");
                NavexaPortfolios = portfolios;
                return Page();
            }
            await sync.ConnectAsync(Portfolio!.PortfolioId, key, chosen.Id, chosen.Name, CurrentPortfolio.UserId(User));
            Done = $"Connected to the Navexa portfolio \"{chosen.Name}\". Check the settings below, then press Check Navexa.";
            return RedirectToPage();
        }
        catch (NavexaApiException ex)
        {
            ModelState.AddModelError("", ex.Message);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostSettingsAsync()
    {
        if (!await LoadAsync(withPlan: false) || Connection is null)
            return RedirectToPage();
        var bank = TradeBankAccount?.Trim();
        if (bank is { Length: > 500 })
            ModelState.AddModelError(nameof(TradeBankAccount), "The bank account name can be at most 500 characters.");
        if (ImportFrom is { } from && from > DateOnly.FromDateTime(DateTime.Today))
            ModelState.AddModelError(nameof(ImportFrom), "The start date can't be in the future.");
        if (!ModelState.IsValid)
        {
            Plan = await sync.GetSavedPlanAsync(Portfolio!.PortfolioId);
            return Page();
        }
        await sync.SaveSettingsAsync(Portfolio!.PortfolioId, bank, ImportFrom, Schedule);
        Done = "Settings saved.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostMappingsAsync()
    {
        if (!await LoadAsync(withPlan: false) || Connection is null)
            return RedirectToPage();

        var mappings = new List<NavexaMapping>();
        foreach (var h in Holdings.Where(h => h.Key.Length > 0))
        {
            var exchange = h.Exchange?.Trim().ToUpperInvariant();
            var code = h.Code?.Trim().ToUpperInvariant();
            if (h.Skip)
                mappings.Add(new(NavexaMapping.Holding, h.Key, h.Label, true, null, null, null));
            else if (string.IsNullOrEmpty(exchange) && string.IsNullOrEmpty(code))
                continue;
            else if (string.IsNullOrEmpty(exchange) || string.IsNullOrEmpty(code) || exchange.Length > 50 || code.Length > 50 || code.Contains(' '))
                ModelState.AddModelError("", $"For \"{h.Label}\", enter an exchange and a code of up to 50 characters with no spaces, or tick Don't bring across.");
            else
                mappings.Add(new(NavexaMapping.Holding, h.Key, h.Label, false, exchange, code, null));
        }
        foreach (var c in CashAccounts.Where(c => c.Key.Length > 0))
        {
            var bank = c.BankAccount?.Trim();
            if (c.Skip)
                mappings.Add(new(NavexaMapping.CashAccount, c.Key, c.Label, true, null, null, null));
            else if (string.IsNullOrEmpty(bank))
                continue;
            else if (bank.Length > 500)
                ModelState.AddModelError("", $"The bank account name for \"{c.Label}\" can be at most 500 characters.");
            else
                mappings.Add(new(NavexaMapping.CashAccount, c.Key, c.Label, false, null, bank, null));
        }
        foreach (var t in CashTypes.Where(t => t.Key.Length > 0))
        {
            if (t.AccountId is { } id && !LedgerAccounts.Any(a => a.AccountId == id))
                ModelState.AddModelError("", $"Choose a ledger account from the list for \"{t.Label}\".");
            else
                mappings.Add(new(NavexaMapping.CashType, t.Key, t.Label, false, null, null, t.AccountId));
        }
        if (!ModelState.IsValid)
        {
            Plan = await sync.GetSavedPlanAsync(Portfolio!.PortfolioId);
            return Page();
        }
        await sync.SaveMappingsAsync(Portfolio!.PortfolioId, mappings);
        Done = "Saved. Records waiting on these choices are now ready to add, unless they need something else.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostCheckAsync()
    {
        if (!await LoadAsync(withPlan: false) || Connection is null)
            return RedirectToPage();
        try
        {
            var plan = await sync.CheckAsync(Portfolio!.PortfolioId, HttpContext.RequestAborted);
            Done = $"Checked Navexa: {plan.Count(SyncStatus.Ready):N0} ready to add, {plan.Count(SyncStatus.NeedsInput):N0} need your input. " +
                   "Nothing has been added yet.";
        }
        catch (NavexaApiException ex)
        {
            Problems = ex.Message;
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostAddAsync()
    {
        if (!await LoadAsync(withPlan: false) || Connection is null)
            return RedirectToPage();
        await SaveInputsAsync();
        if (!ModelState.IsValid)
        {
            Plan = await sync.GetSavedPlanAsync(Portfolio!.PortfolioId);
            return Page();
        }
        try
        {
            var result = await sync.AddReadyAsync(Portfolio!.PortfolioId, HttpContext.RequestAborted);
            Report(result);
        }
        catch (NavexaApiException ex)
        {
            Problems = ex.Message;
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostSyncNowAsync()
    {
        if (!await LoadAsync(withPlan: false) || Connection is null)
            return RedirectToPage();
        try
        {
            Report(await sync.SyncAsync(Portfolio!.PortfolioId, HttpContext.RequestAborted));
        }
        catch (NavexaApiException ex)
        {
            Problems = ex.Message;
        }
        return RedirectToPage();
    }

    /// <summary>Connects to a Navexa portfolio id the user typed, without needing it in Navexa's list.</summary>
    private async Task<IActionResult> ConnectToAsync(string key, int navexaPortfolioId, string errorField)
    {
        if (navexaPortfolioId <= 0)
        {
            ModelState.AddModelError(errorField, "The Navexa portfolio ID is a whole number above 0.");
            return Page();
        }
        try
        {
            var name = await sync.NavexaPortfolioNameAsync(key, navexaPortfolioId, HttpContext.RequestAborted);
            await sync.ConnectAsync(Portfolio!.PortfolioId, key, navexaPortfolioId, name, CurrentPortfolio.UserId(User));
            Done = $"Connected to the Navexa portfolio \"{name}\" (Navexa ID {navexaPortfolioId}). Check the settings below, then press Check Navexa.";
            return RedirectToPage();
        }
        catch (NavexaApiException ex)
        {
            ModelState.AddModelError(errorField, ex.Message);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostChangeNavexaPortfolioAsync()
    {
        if (!await LoadAsync(withPlan: false) || Connection is null)
            return RedirectToPage();
        if (TypedNavexaPortfolioId is not { } typed || typed <= 0)
        {
            ModelState.AddModelError(nameof(TypedNavexaPortfolioId), "Enter the Navexa portfolio ID, a whole number above 0.");
            return Page();
        }
        var key = sync.UnprotectKey(Connection.ProtectedKey);
        if (key is null)
        {
            ModelState.AddModelError("", "The saved Navexa API key can't be read any more. Disconnect and enter it again.");
            return Page();
        }
        try
        {
            var name = await sync.NavexaPortfolioNameAsync(key, typed, HttpContext.RequestAborted);
            await sync.SetNavexaPortfolioAsync(Portfolio!.PortfolioId, typed, name);
            Done = $"Now syncing from the Navexa portfolio \"{name}\" (Navexa ID {typed}). Press Check Navexa to see what it holds.";
            return RedirectToPage();
        }
        catch (NavexaApiException ex)
        {
            ModelState.AddModelError(nameof(TypedNavexaPortfolioId), ex.Message);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostDisconnectAsync()
    {
        if (!await LoadAsync(withPlan: false) || Connection is null)
            return RedirectToPage();
        await sync.DisconnectAsync(Portfolio!.PortfolioId);
        Done = "Disconnected and the API key deleted. Everything already added stays in this portfolio.";
        return RedirectToPage();
    }

    private void Report(SyncRunResult result)
    {
        logger.LogInformation("Navexa sync into portfolio {PortfolioId}: {Summary}", Portfolio!.PortfolioId, result.Summary);
        Done = $"Navexa sync: {result.Summary}";
        if (result.Errors.Count > 0)
            Problems = "Some records couldn't be added and will be tried again next time: " + string.Join(" | ", result.Errors.Take(10)) +
                       (result.Errors.Count > 10 ? $" (and {result.Errors.Count - 10} more)" : "");
    }

    private async Task SaveInputsAsync()
    {
        var inputs = new Dictionary<string, SyncInput>();
        foreach (var row in Inputs.Where(r => r.Key.Length > 0))
        {
            if (row.FrankingCredit is < 0 || row.DrpPrice is < 0 || row.DrpUnits is < 0 || row.SplitNewUnits is < 0 || row.SplitOldUnits is < 0)
            {
                ModelState.AddModelError("", "Amounts and units can't be negative.");
                return;
            }
            var input = new SyncInput
            {
                FrankingCredit = row.FrankingCredit,
                DrpUnits = row.DrpUnits,
                DrpPrice = row.DrpPrice,
                SplitNewUnits = row.SplitNewUnits,
                SplitOldUnits = row.SplitOldUnits,
                AddAnyway = row.Duplicate == "add",
                AlreadyHere = row.Duplicate == "same",
            };
            if (input is { FrankingCredit: null, DrpUnits: null, DrpPrice: null, SplitNewUnits: null, SplitOldUnits: null, AddAnyway: false, AlreadyHere: false })
                continue;
            inputs[row.Key] = input;
        }
        if (inputs.Count > 0)
            await sync.SaveInputsAsync(Portfolio!.PortfolioId, inputs);
    }

    private async Task<bool> LoadAsync(bool withPlan)
    {
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is null)
            return false;
        var id = Portfolio.PortfolioId;
        Connection = await sync.GetConnectionAsync(id);
        BankAccounts = (await cash.GetBankAccountsAsync(id)).Select(a => a.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        LedgerAccounts = await ledger.GetJournalAccountsAsync(id);
        AccountingMode = await ledger.GetAccountingModeAsync(id);
        if (Connection is null)
            return true;

        TradeBankAccount ??= Connection.TradeBankAccount;
        ImportFrom ??= Connection.ImportFrom is { } d ? DateOnly.FromDateTime(d) : null;
        if (Request.Method == "GET")
            Schedule = Connection.Schedule;
        ImportedCount = await sync.CountImportedAsync(id);
        if (withPlan)
            Plan = await sync.GetSavedPlanAsync(id);
        return true;
    }

    /// <summary>The mapping rows to show: what's saved, plus anything the last check found unmapped (with suggestions).</summary>
    public async Task<(List<HoldingRow> Holdings, List<CashAccountRow> Cash, List<CashTypeRow> Types)> MappingRowsAsync()
    {
        var saved = Portfolio is null ? [] : await sync.GetMappingsAsync(Portfolio.PortfolioId);
        // Mappings to the old "CUSTOM" suggestion are no longer used (custom holdings are mapped automatically), so they aren't shown
        var holdings = saved.Where(m => m.Kind == NavexaMapping.Holding && (m.Skip || !NavexaCustomAssets.IsCustomExchange(m.Exchange)))
            .Select(m => new HoldingRow { Key = m.Key, Label = m.Label, Skip = m.Skip, Exchange = m.Exchange, Code = m.Code }).ToList();
        var cashRows = saved.Where(m => m.Kind == NavexaMapping.CashAccount)
            .Select(m => new CashAccountRow { Key = m.Key, Label = m.Label, Skip = m.Skip, BankAccount = m.Code }).ToList();
        var types = saved.Where(m => m.Kind == NavexaMapping.CashType)
            .Select(m => new CashTypeRow { Key = m.Key, Label = m.Label, AccountId = m.AccountId }).ToList();
        if (Plan is { } plan)
        {
            holdings.InsertRange(0, plan.Setup.Holdings.Where(n => holdings.All(h => h.Key != n.Key))
                .Select(n => new HoldingRow { Key = n.Key, Label = n.Label, Exchange = n.SuggestedExchange, Code = n.SuggestedCode }));
            cashRows.InsertRange(0, plan.Setup.CashAccounts.Where(n => cashRows.All(c => c.Key != n.Key))
                .Select(n => new CashAccountRow { Key = n.Key, Label = n.Label }));
            types.InsertRange(0, plan.Setup.CashTypes.Where(n => types.All(t => t.Key != n.Key))
                .Select(n => new CashTypeRow { Key = n.Key, Label = n.Label, AccountId = SuggestLedgerAccount(n.Key) }));
        }
        return (holdings, cashRows, types);
    }

    /// <summary>Interest received to Interest Income, interest charged and fees to an expense account, when the portfolio has one by that name.</summary>
    private int? SuggestLedgerAccount(string type)
    {
        string[] words = type switch
        {
            "interest_payment" => ["interest income"],
            "interest_charged" => ["interest expense", "interest paid", "borrowing"],
            "fee" => ["bank fees", "bank charges", "account fees", "fees"],
            "income_payout" => ["other investment income", "other income"],
            _ => [],
        };
        foreach (var w in words)
            if (LedgerAccounts.FirstOrDefault(a => a.DisplayName.Contains(w, StringComparison.OrdinalIgnoreCase)) is { } hit)
                return hit.AccountId;
        return null;
    }

    public static string StatusBadge(SyncStatus status) => status switch
    {
        SyncStatus.Ready => "bg-success",
        SyncStatus.NeedsInput => "bg-primary",
        SyncStatus.Waiting => "bg-info text-dark",
        SyncStatus.Imported => "bg-secondary",
        SyncStatus.Failed => "bg-danger",
        _ => "bg-warning text-dark",
    };

    public static string StatusText(SyncStatus status) => status switch
    {
        SyncStatus.Ready => "Ready",
        SyncStatus.NeedsInput => "Needs input",
        SyncStatus.Waiting => "Waiting",
        SyncStatus.Imported => "Added",
        SyncStatus.Failed => "Failed",
        _ => "Not brought across",
    };

    public static string KindText(SyncKind kind) => kind switch
    {
        SyncKind.Trade => "Trade",
        SyncKind.Payout => "Payout",
        SyncKind.Drp => "DRP",
        SyncKind.Cash => "Cash",
        SyncKind.Split => "Split",
        SyncKind.CapitalReturn => "Return of capital",
        _ => "",
    };

    /// <summary>A one-line summary of what will be posted, in the portfolio's currency.</summary>
    public static string WillPost(PlannedItem i) => i switch
    {
        { Trade: { } t } => $"{t.Type} {t.Quantity:#,##0.########} {t.Exchange}:{t.Code} at {t.Price:0.00####} {t.Currency}" +
                            (t.Brokerage > 0 ? $", brokerage {t.Brokerage:0.00}" : "") +
                            (t.BankAccount is null ? ", no cash entry" : $", settles in {t.BankAccount}"),
        { Payout: { } p } => $"{p.Exchange}:{p.Code} cash {p.Net:C}, franked {p.Franked:C}, unfranked {p.Unfranked:C}, franking credit {p.FrankingCredit:C}" +
                             (p.ForeignTax > 0 ? $", foreign tax {p.ForeignTax:C}" : ""),
        { Drp: { } d } => $"{d.Exchange}:{d.Code} dividend {d.Franked + d.Unfranked:C} reinvested in {d.Units:#,##0} units at {d.Price:0.0000}, franking credit {d.FrankingCredit:C}",
        { Cash: { } c } => $"{(c.Deposit > 0 ? "Deposit" : "Withdrawal")} {c.Deposit + c.Withdrawal:0.00} {c.Currency} in {c.BankAccount}",
        { Split: { } s } => $"{s.Exchange}:{s.Code} {s.NewUnits:N0} for {s.OldUnits:N0}",
        { CapitalReturn: { } r } => $"{r.Exchange}:{r.Code} return of capital {r.Total:C}",
        _ => "",
    };
}
