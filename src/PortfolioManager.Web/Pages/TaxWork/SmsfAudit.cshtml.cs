using Microsoft.AspNetCore.Mvc;
using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Tax;
using PortfolioManager.Core.TaxWork;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.TaxWork;

/// <summary>The bank reconciliation an audit pack shows for one bank account at year end.</summary>
public sealed record AuditBankRec(string Account, BankReconciliation? Reconciliation, IReadOnlyList<BankRecLine> Outstanding)
{
    public decimal OutstandingTotal => Outstanding.Sum(l => l.Amount);
}

/// <summary>A printable audit pack for an SMSF: member statements, investments at market value, bank reconciliations and tax.</summary>
public class SmsfAuditModel(CurrentPortfolio current, TaxWorkpaperService workpapers, LockedPeriodService locks,
    UnrealisedGainsService unrealised, BankReconciliationService recs, Core.Practice.AttachmentService attachments) : TaxYearPageModel(current, workpapers, locks)
{
    public UnrealisedGainsReport? Investments { get; private set; }
    public IReadOnlyList<AuditBankRec> BankRecs { get; private set; } = [];
    public IReadOnlyList<Core.Practice.Attachment> Attachments { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync()
    {
        if (await LoadAsync() is { } refusal)
            return refusal;
        if (Portfolio is null)
            return Page();

        var id = Portfolio.PortfolioId;
        Investments = await unrealised.GetAsync(id, YearEnd, groupByStock: true);
        var list = new List<AuditBankRec>();
        foreach (var account in await recs.GetAccountsAsync(id))
        {
            if (account.DisplayName.Contains(BankImportService.SuspenseAccountName))
                continue;
            var rec = (await recs.GetHistoryAsync(id, account.AccountId))
                .Where(r => r.IsComplete && r.StatementDate <= YearEnd)
                .MaxBy(r => r.StatementDate);
            list.Add(new AuditBankRec(account.DisplayName, rec, rec is null ? [] : await recs.GetUnpresentedAsync(id, rec)));
        }
        BankRecs = list;
        Attachments = await attachments.ListAsync(id, year: Year);
        return Page();
    }
}
