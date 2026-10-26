using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Clients;
using PortfolioManager.Core.Payouts;
using PortfolioManager.Core.Regions;
using PortfolioManager.Core.Statements;
using PortfolioManager.Core.Tax;

namespace PortfolioManager.Core.TaxWork;

/// <summary>Everything the tax pages work from for one portfolio and financial year.</summary>
public sealed record TaxWorkpaper(
    ClientDetails Client,
    int Year,
    DateOnly YearStart,
    DateOnly YearEnd,
    TaxYearData Data,
    LedgerYear Ledger,
    CgtReturnReport CgtReport,
    TaxIncomeFacts Income,
    TaxReconciliation Reconciliation,
    TaxCalculation? Tax,
    IReadOnlyList<string> Warnings,
    SmsfYearData? Smsf = null,
    IReadOnlyList<SmsfMemberSummary>? Members = null,
    EssYear? EmployeeShares = null,
    TradingStockReport? Trading = null,
    RentalSchedule? Rental = null)
{
    /// <summary>The year's employee share scheme discounts (individuals only).</summary>
    public EssYear Ess => EmployeeShares ?? EssYear.None;

    public string EntityType => Client.EntityType;
    public CgtSummary Cgt => Reconciliation.Cgt;
    public string YearLabel => FinancialYear.Label(Year);
}

/// <summary>
/// Builds the income tax workpaper from the ledger (trial balances at the two year ends), the
/// FIFO capital gains, the payouts and the year's saved settings. Nothing is written.
/// </summary>
public sealed class TaxWorkpaperService(
    ClientService clients,
    LedgerReportService reports,
    CgtDiscountService cgt,
    PayoutService payouts,
    TaxYearService years,
    SmsfMemberService members,
    FundTaxStatementService statements,
    EssStatementService ess,
    TradingStockService tradingStock,
    RentalService rentals)
{
    /// <summary>Null when the portfolio doesn't exist. The caller passes the selected portfolio's id.</summary>
    public async Task<TaxWorkpaper?> BuildAsync(int portfolioId, int year)
    {
        var client = await clients.GetAsync(portfolioId);
        if (client is null)
            return null;

        var start = FinancialYear.StartDate(year);
        var end = FinancialYear.EndDate(year);
        var ledger = new LedgerYear(
            LedgerSnapshot.FromTrialBalance(start.AddDays(-1), await reports.TrialBalanceAsync(portfolioId, start.AddDays(-1))),
            LedgerSnapshot.FromTrialBalance(end, await reports.TrialBalanceAsync(portfolioId, end)));
        var data = await years.GetAsync(portfolioId, year);
        var cgtReport = await cgt.GetReturnAsync(portfolioId, client.EntityType, FinancialYear.Resolve(year, null, null, start));
        var income = TaxIncomeFacts.WithStatements(
            (await payouts.GetPayoutsAsync(portfolioId, start.ToDateTime(TimeOnly.MinValue), end.ToDateTime(TimeOnly.MinValue))).Payouts,
            await statements.GetAsync(portfolioId), year);
        var smsf = client.EntityType == ClientEntityTypes.Smsf ? await members.GetAsync(portfolioId, year) : null;
        var essYear = EssYear.For(await ess.GetAsync(portfolioId), year);
        var trading = await tradingStock.GetYearAsync(portfolioId, year);
        var w = Assemble(client, year, ledger, data, cgtReport, income, smsf, RegionSettings.Current.IsAustralia, essYear, trading);
        // Rental properties are kept outside the ledger, so they add return labels but not to the reconciliation
        return client.EntityType == ClientEntityTypes.Individual ? w with { Rental = await rentals.GetYearAsync(portfolioId, year) } : w;
    }

    public static TaxWorkpaper Assemble(
        ClientDetails client, int year, LedgerYear ledger, TaxYearData data, CgtReturnReport cgtReport,
        TaxIncomeFacts income, SmsfYearData? smsf = null, bool australia = true, EssYear? ess = null, TradingStockReport? trading = null)
    {
        var summary = data.Settings.CapitalLossesBroughtForward is { } losses
            ? CgtDiscount.Summarise(cgtReport.Events, cgtReport.Summary.Rate, losses)
            : cgtReport.Summary;
        // Shares held as trading stock make a trading profit instead of capital gains; losses brought forward wait for a later gain.
        // Holdings a trader keeps as investments still make capital gains.
        if (trading is not null)
            summary = CgtDiscount.Summarise(cgtReport.Events.Where(trading.StaysCapital).ToList(), cgtReport.Summary.Rate, summary.PriorYearLosses);
        var rec = TaxReconciliation.Build(client.EntityType, ledger, summary, income, data, smsf?.TaxInputs, trading?.Profit);
        var tax = TaxCalculation.For(client.EntityType, rec, income, data.Settings);

        var warnings = new List<string>();
        if (!australia)
            warnings.Add("This portfolio isn't set to Australia, but these workpapers follow Australian tax rules.");
        if (!client.Saved)
            warnings.Add($"The client details haven't been saved, so the entity type ({ClientEntityTypes.Describe(client.EntityType)}) is a guess from the portfolio. Check it under Clients > Client details.");
        if (cgtReport.Problems.Count > 0)
            warnings.Add($"{cgtReport.Problems.Count} sale(s) couldn't be fully matched to purchases, so the capital gain may be wrong. See Reports > Realised capital gains.");
        if (ess is { TotalDiscount: > 0 } && client.EntityType is ClientEntityTypes.Company or ClientEntityTypes.Trust or ClientEntityTypes.Partnership or ClientEntityTypes.Smsf)
            warnings.Add("Employee share scheme statements are entered for this year, but only individuals report ESS discounts; they're left out of this return.");
        if (trading is { Problems.Count: > 0 })
            warnings.Add($"Trading stock: {string.Join(" ", trading.Problems)}");
        if (ledger.Accounts.Count == 0)
            warnings.Add("Nothing has been posted to this portfolio's ledger, so the accounting profit is nil.");

        IReadOnlyList<SmsfMemberSummary>? summaries = null;
        if (smsf is not null)
        {
            // Members share the investment earnings; contributions are credited to them directly
            summaries = SmsfAllocation.Build(year, smsf, ledger.ProfitBeforeTax - ledger.Contributions, tax?.TaxAfterOffsets ?? 0, FinancialYear.StartDate(year));
            if (smsf.Members.Count == 0)
                warnings.Add("No fund members have been entered yet (Tax > SMSF members), so non-concessional contributions aren't taken out of taxable income.");
        }

        return new TaxWorkpaper(client, year, FinancialYear.StartDate(year), FinancialYear.EndDate(year),
            data, ledger, cgtReport, income, rec, tax, warnings, smsf, summaries, ess, trading);
    }
}
