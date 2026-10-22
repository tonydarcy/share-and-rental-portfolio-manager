using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Core.SalesTax;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Accounting;

public class JournalEntryModel(CurrentPortfolio current, JournalService journals, RecurringJournalService repeating, ChartOfAccountsService chart,
    SalesTaxService salesTax, TrackingService tracking, ILogger<JournalEntryModel> logger) : PageModel
{
    /// <summary>A repeating journal being changed (nothing is posted), or null.</summary>
    [BindProperty(SupportsGet = true)]
    public int? Template { get; set; }

    /// <summary>Keep a new journal as a repeating one.</summary>
    [BindProperty]
    public bool Repeats { get; set; }

    [BindProperty]
    public RecurringJournalInput Repeat { get; set; } = new();

    /// <summary>Post a reversal of a new journal on this date (an accrual that unwinds).</summary>
    [BindProperty]
    public DateTime? ReverseOn { get; set; }

    /// <summary>The manual journal being edited, or null for a new one.</summary>
    [BindProperty(SupportsGet = true)]
    public int? Id { get; set; }

    [BindProperty]
    public JournalInput Input { get; set; } = JournalInput.New(DateTime.Today);

    public PortfolioSummary? Portfolio { get; private set; }
    public string Mode { get; private set; } = "";
    public IReadOnlyList<AccountChoice> Accounts { get; private set; } = [];

    /// <summary>The active tax codes; the tax columns only show when there are some.</summary>
    public IReadOnlyList<TaxCode> TaxCodes { get; private set; } = [];
    public string TaxName { get; private set; } = "Tax";

    /// <summary>Tracking categories; a column shows for each one set up.</summary>
    public TrackingSetup Tracking { get; private set; } = TrackingSetup.None;

    public async Task<IActionResult> OnGetAsync()
    {
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is null)
            return Page();

        if (Id is { } id)
        {
            // Null unless it belongs to the selected portfolio
            var journal = await journals.GetAsync(Portfolio.PortfolioId, id);
            if (journal is null)
            {
                TempData["Error"] = "That journal wasn't found in this portfolio.";
                return RedirectToPage("/Accounting/Journals");
            }
            if (!journal.IsManual)
            {
                TempData["Error"] = "Only manual journals can be edited. This one was posted for a " +
                                    JournalRules.SourceLabel(journal.SourceModule).ToLowerInvariant() + ".";
                return RedirectToPage("/Accounting/Journal", new { id });
            }
            Input = JournalService.ToInput(journal, (await salesTax.GetCodesAsync(Portfolio.PortfolioId)).ToDictionary(c => c.TaxCodeId));
        }
        else if (Template is { } templateId)
        {
            if (await repeating.GetAsync(Portfolio.PortfolioId, templateId) is not { } t)
            {
                TempData["Error"] = "That repeating journal wasn't found in this portfolio.";
                return RedirectToPage("/Accounting/Repeating");
            }
            Input = t.ToInput(t.Start);
            Repeats = true;
            Repeat = new RecurringJournalInput { Name = t.Name, Frequency = t.Frequency, End = t.End?.ToDateTime(TimeOnly.MinValue), Reverse = t.Reverse };
        }

        await LoadListsAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is null)
            return Forbid();

        Input.Lines ??= [];
        JournalChangeResult result;
        try
        {
            // Validates the lines against this portfolio's accounts, checks an edited journal is
            // its own manual journal, and saves everything in one transaction
            if (Template is { } templateId)
            {
                var taxCodes = (await salesTax.GetActiveCodesAsync(Portfolio.PortfolioId)).ToDictionary(c => c.TaxCodeId);
                var allowed = (await chart.GetJournalAccountsAsync(Portfolio.PortfolioId)).Select(a => a.AccountId).ToHashSet();
                var saved = await repeating.UpdateAsync(Portfolio.PortfolioId, templateId, Input, Repeat, allowed, taxCodes);
                if (saved.Ok)
                {
                    TempData["Message"] = "Repeating journal saved. Nothing was posted.";
                    return RedirectToPage("/Accounting/Repeating");
                }
                ModelState.AddModelError(string.Empty, saved.Error!);
                await LoadListsAsync();
                return Page();
            }
            result = Id is null && Repeats
                ? await repeating.PostAndRepeatAsync(Portfolio.PortfolioId, Input, Repeat)
                : await journals.SaveAsync(Portfolio.PortfolioId, Id, Input);
            if (result.Outcome == JournalChangeOutcome.Saved && Id is null && !Repeats && ReverseOn is { } reverseOn)
                await journals.SaveAsync(Portfolio.PortfolioId, null, JournalReversal.Reverse(Input, DateOnly.FromDateTime(reverseOn)));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Saving journal {JournalId} failed for portfolio {PortfolioId}", Id, Portfolio.PortfolioId);
            ModelState.AddModelError(string.Empty, LockedPeriodService.IsLockError(ex) ? LockedPeriodFilter.Message(ex) : "The journal couldn't be saved, and nothing was changed. Try again.");
            await LoadListsAsync();
            return Page();
        }

        switch (result.Outcome)
        {
            case JournalChangeOutcome.Saved:
                TempData["Message"] = Id is not null ? "Journal updated."
                    : Repeats ? $"Journal posted. It repeats {RepeatFrequencies.Label(Repeat.Frequency).ToLowerInvariant()} (Accounting > Repeating journals)."
                    : ReverseOn is not null ? $"Journal posted, with its reversal on {ReverseOn:d}." : "Journal posted.";
                return RedirectToPage("/Accounting/Journal", new { id = result.JournalId });
            case JournalChangeOutcome.NotFound:
                TempData["Error"] = "That journal wasn't found in this portfolio, so nothing was saved.";
                return RedirectToPage("/Accounting/Journals");
            case JournalChangeOutcome.NotManual:
                TempData["Error"] = "Only manual journals can be edited.";
                return RedirectToPage("/Accounting/Journal", new { id = Id });
        }

        foreach (var error in result.Errors ?? [])
            ModelState.AddModelError(string.Empty, error);
        await LoadListsAsync();
        return Page();
    }

    private async Task LoadListsAsync()
    {
        Accounts = await chart.GetJournalAccountsAsync(Portfolio!.PortfolioId);
        Mode = await chart.GetAccountingModeAsync(Portfolio.PortfolioId);
        Tracking = await tracking.GetAsync(Portfolio.PortfolioId);
        if (await salesTax.GetSetupAsync(Portfolio.PortfolioId) is { } setup)
        {
            TaxName = setup.TaxName;
            TaxCodes = await salesTax.GetActiveCodesAsync(Portfolio.PortfolioId);
        }
        // Always leave room to type one more line
        if (Input.Lines.Count == 0 || !Input.Lines[^1].IsBlank)
            Input.Lines.Add(new JournalLineInput());
        if (Input.Lines.Count < 2)
            Input.Lines.Add(new JournalLineInput());
    }
}
