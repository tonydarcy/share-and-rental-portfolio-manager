using Microsoft.AspNetCore.Mvc;
using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Clients;
using PortfolioManager.Core.Tax;
using PortfolioManager.Core.TaxWork;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.TaxWork;

/// <summary>
/// Tax planning for an individual: the year's investment figures plus what the user enters (salary, PAYG), then the same with a
/// what-if sale, a deductible super contribution and extra deductions, side by side. Nothing is saved.
/// </summary>
public class PlanningModel(CurrentPortfolio current, TaxWorkpaperService workpapers, LockedPeriodService locks) : TaxYearPageModel(current, workpapers, locks)
{
    [BindProperty(SupportsGet = true)] public decimal OtherIncome { get; set; }
    [BindProperty(SupportsGet = true)] public decimal Payg { get; set; }
    [BindProperty(SupportsGet = true)] public decimal OtherDeductions { get; set; }
    [BindProperty(SupportsGet = true)] public decimal? SaleProceeds { get; set; }
    [BindProperty(SupportsGet = true)] public decimal? SaleCost { get; set; }
    [BindProperty(SupportsGet = true)] public bool SaleLong { get; set; } = true;
    [BindProperty(SupportsGet = true)] public decimal Contribution { get; set; }
    [BindProperty(SupportsGet = true)] public decimal ExtraDeductions { get; set; }
    [BindProperty(SupportsGet = true)] public decimal FringeBenefits { get; set; }
    [BindProperty(SupportsGet = true)] public decimal ReportableSuper { get; set; }

    public PlanResult? Now { get; private set; }
    public PlanResult? Plan { get; private set; }
    public IndividualRates Rates { get; private set; } = IndividualRates.For(2026);
    public ContributionCaps Caps { get; private set; } = ContributionCaps.For(2026);
    public bool IsIndividual => Workpaper?.EntityType == ClientEntityTypes.Individual;

    public async Task<IActionResult> OnGetAsync()
    {
        // Planning is usually for the year under way
        Fy ??= FinancialYear.EndYearFor(Today);
        if (await LoadAsync() is { } refusal)
            return refusal;
        if (!IsIndividual)
            return Page();
        var w = Workpaper!;
        Rates = IndividualRates.For(Year);
        Caps = ContributionCaps.For(Year);
        var investment = w.Reconciliation.TaxableIncome - w.Cgt.NetCapitalGain;
        // Net investment losses for the ESS income test: a net loss on the rentals, or on the other investments
        var rental = w.Rental?.Net ?? 0;
        var now = new PlanInputs(OtherIncome, investment, rental, w.Cgt.NetCapitalGain, OtherDeductions, 0,
            w.Income.FrankingCredits, w.Income.TfnWithheld + w.Ess.TfnWithheld + Payg, w.Income.ForeignTax,
            w.Ess.UpfrontEligible, w.Ess.UpfrontNotEligible + w.Ess.Deferral + w.Ess.Pre2009, FringeBenefits, ReportableSuper,
            Math.Max(0, -rental) + Math.Max(0, -investment));
        Now = TaxPlanner.Work(now, Rates);
        var sale = SaleProceeds is { } p && SaleCost is { } c ? new WhatIfSale(p, c, SaleLong) : null;
        Plan = TaxPlanner.Work(now with
        {
            NetCapitalGain = TaxPlanner.NetGainWith(w.Cgt, sale),
            Deductions = OtherDeductions + ExtraDeductions,
            ConcessionalDeduction = Math.Max(0, Contribution),
        }, Rates);
        return Page();
    }
}
