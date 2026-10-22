using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Invoicing;
using PortfolioManager.Core.Payouts;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Core.SalesTax;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Accounting;

/// <summary>Payouts or trades the line can be matched to, posted back to the Payout or Trade handler.</summary>
public sealed record MatchList(IReadOnlyList<MatchCandidate> Candidates, int LineId, string Handler)
{
    public const string Payout = "Payout";
    public const string Trade = "Trade";

    public string IdName => Handler == Payout ? "payoutId" : "tradeId";
}

public class AllocateModel(CurrentPortfolio current, BankImportService imports, ChartOfAccountsService chart, AllocationRuleService rules, SalesTaxService salesTax, InvoiceExtrasService invoiceExtras, ILogger<AllocateModel> logger) : PageModel
{
    [BindProperty]
    public int AccountId { get; set; }

    [BindProperty]
    public string? Description { get; set; }

    /// <summary>A GST / VAT / sales tax code: the account gets the amount less tax, and the tax goes to the control account.</summary>
    [BindProperty]
    public int? TaxCodeId { get; set; }

    /// <summary>The tax in the amount, when it isn't the code's rate. Blank: worked out.</summary>
    [BindProperty]
    public decimal? Tax { get; set; }

    [BindProperty]
    public PayoutInput PayoutInput { get; set; } = new();

    /// <summary>Also add an allocation rule sending lines containing RuleText to the account.</summary>
    [BindProperty]
    public bool MakeRule { get; set; }

    [BindProperty]
    public string? RuleText { get; set; }

    public PortfolioSummary? Portfolio { get; private set; }
    public string Mode { get; private set; } = "";
    public UnallocatedLine? Line { get; private set; }
    public IReadOnlyList<AccountChoice> Accounts { get; private set; } = [];
    public IReadOnlyList<TaxCode> TaxCodes { get; private set; } = [];
    public string TaxName { get; private set; } = "Tax";
    public IReadOnlyList<MatchCandidate> Payouts { get; private set; } = [];
    public IReadOnlyList<MatchCandidate> Trades { get; private set; } = [];
    public IReadOnlyList<DepositMatch> InvoiceMatches { get; private set; } = [];

    /// <summary>Which section to open: account, payout, trade or new.</summary>
    public string Open { get; private set; } = "account";

    public async Task<IActionResult> OnGetAsync(int id)
    {
        if (await LoadAsync(id) is { } refusal)
            return refusal;

        Description = Line!.Description;
        RuleText = AllocationRule.Suggest(Line.Description);
        PayoutInput = new PayoutInput
        {
            Date = Line.Date.ToDateTime(TimeOnly.MinValue),
            Total = Line.IsDeposit ? Line.Amount : 0,
            StockExchangeCode = "ASX",
        };
        Open = Payouts.Count > 0 ? "payout" : InvoiceMatches.Any(m => m.Exact) ? "invoice" : Trades.Count > 0 ? "trade" : "account";
        return Page();
    }

    public async Task<IActionResult> OnPostAccountAsync(int id)
    {
        if (await LoadAsync(id) is { } refusal)
            return refusal;

        // The service checks the account is one of this portfolio's postable accounts and the line is its own
        var result = await imports.AllocateToAccountAsync(Portfolio!.PortfolioId, id, AccountId, Description, TaxCodeId, TaxCodeId is null ? null : Tax);
        var message = $"Assigned to {Accounts.FirstOrDefault(a => a.AccountId == AccountId)?.DisplayName}";
        message += TaxCodeId is { } codeId && TaxCodes.FirstOrDefault(c => c.TaxCodeId == codeId) is { } code ? $" with {TaxName} code {code.Code}." : ".";
        if (result.Ok && MakeRule && !string.IsNullOrWhiteSpace(RuleText))
        {
            var rule = await rules.SaveAsync(Portfolio.PortfolioId, new AllocationRuleInput { Contains = RuleText, AccountId = AccountId, TaxCodeId = TaxCodeId });
            message += rule.Ok
                ? $" Lines containing \"{RuleText.Trim()}\" will go there from now on."
                : $" The rule wasn't added: {rule.Error}";
        }
        return Done(result, "account", message);
    }

    public async Task<IActionResult> OnPostPayoutAsync(int id, int payoutId)
    {
        if (await LoadAsync(id) is { } refusal)
            return refusal;
        return Done(await imports.MatchPayoutAsync(Portfolio!.PortfolioId, id, payoutId), "payout", "Matched to the payout.");
    }

    public async Task<IActionResult> OnPostTradeAsync(int id, int tradeId)
    {
        if (await LoadAsync(id) is { } refusal)
            return refusal;
        return Done(await imports.MatchTradeAsync(Portfolio!.PortfolioId, id, tradeId), "trade", "Matched to the trade.");
    }

    public async Task<IActionResult> OnPostInvoiceAsync(int id, int invoiceId)
    {
        if (await LoadAsync(id) is { } refusal)
            return refusal;
        var r = await invoiceExtras.MatchDepositAsync(Portfolio!.PortfolioId, id, invoiceId);
        var number = InvoiceMatches.FirstOrDefault(m => m.InvoiceId == invoiceId)?.Number;
        return Done(r.Ok ? AllocationResult.Success : new AllocationResult(r.Error), "invoice", $"Recorded as a payment of invoice {number}.");
    }

    public async Task<IActionResult> OnPostNewPayoutAsync(int id)
    {
        if (await LoadAsync(id) is { } refusal)
            return refusal;

        // The cash received is always the deposit's amount
        PayoutInput.Total = Line!.Amount;
        foreach (var error in PayoutInput.Validate())
            ModelState.AddModelError(string.Empty, error);
        if (!ModelState.IsValid)
        {
            Open = "new";
            return Page();
        }

        AllocationResult result;
        try
        {
            result = await imports.AddPayoutAndMatchAsync(Portfolio!.PortfolioId, id, PayoutInput);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Recording a payout for imported line {LineId} failed for portfolio {PortfolioId}", id, Portfolio!.PortfolioId);
            result = new AllocationResult(LockedPeriodService.IsLockError(ex) ? LockedPeriodFilter.Message(ex) : "The payout couldn't be recorded and matched, so nothing was saved.");
        }
        return Done(result, "new", $"Recorded the {PayoutInput.StockCode.Trim().ToUpperInvariant()} payout of {Line.Amount:C} and matched it.");
    }

    private IActionResult Done(AllocationResult result, string section, string message)
    {
        if (result.Ok)
        {
            TempData["Message"] = message;
            return RedirectToPage("/Accounting/Unallocated");
        }
        ModelState.AddModelError(string.Empty, result.Error!);
        Open = section;
        return Page();
    }

    /// <summary>The line must be an unallocated suspense line of the selected portfolio.</summary>
    private async Task<IActionResult?> LoadAsync(int id)
    {
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is null)
            return Forbid();

        Line = await imports.GetLineAsync(Portfolio.PortfolioId, id);
        if (Line is null)
        {
            TempData["Error"] = "That transaction wasn't found in this portfolio, or it has already been allocated.";
            return RedirectToPage("/Accounting/Unallocated");
        }
        Mode = await chart.GetAccountingModeAsync(Portfolio.PortfolioId);
        Accounts = await chart.GetJournalAccountsAsync(Portfolio.PortfolioId);
        if (await salesTax.GetSetupAsync(Portfolio.PortfolioId) is { } setup)
        {
            TaxName = setup.TaxName;
            TaxCodes = await salesTax.GetActiveCodesAsync(Portfolio.PortfolioId);
        }
        Payouts = await imports.GetPayoutMatchesAsync(Portfolio.PortfolioId, id);
        Trades = await imports.GetTradeMatchesAsync(Portfolio.PortfolioId, id);
        if (Line.IsDeposit)
            InvoiceMatches = await invoiceExtras.GetDepositMatchesAsync(Portfolio.PortfolioId, Line.Amount);
        return null;
    }
}
