using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Bills;
using PortfolioManager.Core.Clients;
using PortfolioManager.Core.Dashboard;
using PortfolioManager.Core.Debt;
using PortfolioManager.Core.Holdings;
using PortfolioManager.Core.Invoicing;
using PortfolioManager.Core.NavexaSync;
using PortfolioManager.Core.Payouts;
using PortfolioManager.Core.Performance;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Core.Practice;
using PortfolioManager.Core.Regions;
using PortfolioManager.Core.Rentals;
using PortfolioManager.Core.Reports;
using PortfolioManager.Core.SalesTax;
using PortfolioManager.Core.Tax;
using PortfolioManager.Core.TaxWork;
using PortfolioManager.Core.Trades;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages;

/// <summary>
/// The dashboard. The totals at the top load with the page; every card below loads on its own from
/// <see cref="OnGetSectionAsync"/>, so a slow report (the tax year, the value chart) can't hold up the rest.
/// </summary>
public class IndexModel(
    CurrentPortfolio current,
    DashboardService dashboard,
    RentalService rentals,
    LoanService loans,
    IServiceProvider services,
    ILogger<IndexModel> logger) : PageModel
{
    /// <summary>The cards that load on their own, in the order they're asked for.</summary>
    public static readonly IReadOnlyList<string> Sections =
        ["summary", "chart", "attention", "holdings", "income", "tax", "property", "business", "activity", "clients"];

    public PortfolioSummary? Portfolio { get; private set; }
    public RegionSettings Region { get; private set; } = RegionSettings.Australia;
    public PortfolioValue Value { get; private set; } = new();
    public DateOnly Today { get; } = DateOnly.FromDateTime(DateTime.Today);
    public bool HasClients { get; private set; }

    /// <summary>Rental properties with a value entered: the owner's share of the value less linked loans.</summary>
    public IReadOnlyList<RentalYield> Properties { get; private set; } = [];
    public decimal PropertyEquity => Properties.Sum(p => p.Equity);

    // Value chart choices, kept in the address so a reload keeps them
    [BindProperty(SupportsGet = true)] public string? Period { get; set; }
    [BindProperty(SupportsGet = true)] public string? Bm { get; set; }
    [BindProperty(SupportsGet = true)] public string? View { get; set; }
    [BindProperty(SupportsGet = true)] public string? Alloc { get; set; }

    public string Section { get; private set; } = "";

    /// <summary>True when the value figures couldn't be loaded (e.g. the database was slow to wake), so the page shows dashes, not zeros.</summary>
    public bool ValueFailed { get; private set; }

    public async Task OnGetAsync()
    {
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is null)
            return;
        var id = Portfolio.PortfolioId;
        Region = await current.GetRegionAsync();
        // A slow or failed value query shows dashes and a note instead of an error page; the cards below load on their own
        try
        {
            Value = await dashboard.GetValueAsync(id);
        }
        catch (Exception ex) when (ex is Microsoft.Data.SqlClient.SqlException or TimeoutException)
        {
            logger.LogWarning(ex, "Dashboard value failed for portfolio {PortfolioId}", id);
            ValueFailed = true;
        }
        Properties = await PropertiesAsync(id);
        HasClients = (await current.GetAccessibleAsync()).Count > 1;
        Period = DashboardFigures.NormalPeriod(Period);
    }

    /// <summary>One card's HTML, fetched by the dashboard's script (or opened directly without it).</summary>
    public async Task<IActionResult> OnGetSectionAsync(string? name)
    {
        Section = name?.ToLowerInvariant() ?? "";
        if (!Sections.Contains(Section))
            return NotFound();
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is null)
            return Content("");
        Region = await current.GetRegionAsync();
        Period = DashboardFigures.NormalPeriod(Period);
        var id = Portfolio.PortfolioId;
        try
        {
            switch (Section)
            {
                case "summary": await LoadSummaryAsync(id); break;
                case "chart": await LoadChartAsync(id); break;
                case "attention": await LoadAttentionAsync(id); break;
                case "holdings": await LoadHoldingsAsync(id); break;
                case "income": await LoadIncomeAsync(id); break;
                case "tax": await LoadTaxAsync(id); break;
                case "property": await LoadPropertyAsync(id); break;
                case "business": await LoadBusinessAsync(id); break;
                case "activity": await LoadActivityAsync(id); break;
                case "clients": await LoadClientsAsync(); break;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Dashboard card {Section} failed for portfolio {PortfolioId}", Section, id);
            return Partial("Dashboard/_Failed", this);
        }
        return Partial("Dashboard/_" + char.ToUpperInvariant(Section[0]) + Section[1..], this);
    }

    private T Get<T>() where T : notnull => services.GetRequiredService<T>();

    private async Task<IReadOnlyList<RentalYield>> PropertiesAsync(int id)
    {
        var year = FinancialYear.LastCompleted(Today, Region.YearStart);
        var schedule = await rentals.GetYearAsync(id, year);
        return (await Rentals.IndexModel.YieldsAsync(rentals, loans, id, schedule)).Where(p => p.Property.Active && p.Value is > 0).ToList();
    }

    // ---- Performance strip ----

    public DayMove? Move { get; private set; }
    public PerformanceSummary? YearToDate { get; private set; }
    public HoldingsTotal? OpenTotal { get; private set; }
    public int FinancialYearEnd { get; private set; }
    public decimal YearIncome { get; private set; }
    public decimal YearFranking { get; private set; }

    private async Task LoadSummaryAsync(int id)
    {
        FinancialYearEnd = FinancialYear.EndYearFor(Today, Region.YearStart);
        var from = FinancialYear.StartDate(FinancialYearEnd, Region.YearStart);
        // Start a fortnight earlier so the latest move can be found on the first days of the year
        var perf = await Get<PerformanceService>().GetAsync(id, from.AddDays(-14), Today);
        Move = DashboardFigures.LatestMove(perf.Days);
        var year = perf.Days.Where(d => d.Date >= from).ToList();
        if (year.Count > 0)
        {
            var start = perf.Days.LastOrDefault(d => d.Date < from)?.Value ?? 0;
            var cumulativeBefore = perf.Days.LastOrDefault(d => d.Date < from)?.Cumulative ?? 0;
            var twr = (1 + year[^1].Cumulative) / (1 + cumulativeBefore) - 1;
            YearToDate = new PerformanceSummary(from, Today, start, year[^1].Value, year.Sum(d => d.MoneyIn), year.Sum(d => d.MoneyOut),
                year.Sum(d => d.Income), twr, null);
        }
        OpenTotal = (await Get<HoldingsService>().GetHoldingsAsync(id, null, true)).Total;
        // Every payout this year, reinvested ones too; rent is on the property card
        var payouts = (await Get<PayoutService>().GetPayoutsAsync(id, from.ToDateTime(TimeOnly.MinValue), Today.ToDateTime(TimeOnly.MinValue))).Payouts
            .Where(p => !p.IsRental).ToList();
        YearIncome = payouts.Sum(p => p.Total);
        YearFranking = payouts.Sum(p => p.FrankingCredit);
    }

    // ---- Value chart ----

    public PerformanceResult? Perf { get; private set; }
    public IReadOnlyList<PerformanceDay> ChartDays { get; private set; } = [];
    public BenchmarkResult? Benchmark { get; private set; }
    public IReadOnlyList<BenchmarkChoice> Choices { get; private set; } = [];
    public bool ShowReturn => string.Equals(View, "return", StringComparison.OrdinalIgnoreCase);

    private async Task LoadChartAsync(int id)
    {
        var performance = Get<PerformanceService>();
        var from = DashboardFigures.PeriodStart(Period, Today, Region.YearStart);
        Perf = await performance.GetAsync(id, from, Today);
        ChartDays = DashboardFigures.SinceFirstHolding(Perf.Days);
        Choices = await performance.GetChoicesAsync();
        Bm = string.IsNullOrWhiteSpace(Bm) ? Choices.FirstOrDefault()?.Key : Bm.Trim();
        if (ShowReturn && ChartDays.Count > 0 && Bm is not null && !Bm.Equals("none", StringComparison.OrdinalIgnoreCase))
            Benchmark = await performance.GetBenchmarkAsync(Bm, Choices, ChartDays[0].Date, Today);
    }

    /// <summary>A link to this page with one chart choice changed; the script reloads just the card.</summary>
    public string ChartLink(string? period = null, string? view = null, string? bm = null, bool section = false)
    {
        var values = new { name = section ? "chart" : null, period = period ?? Period, view = view ?? View, bm = bm ?? Bm, alloc = Alloc };
        return section ? Url.Page("/Index", "Section", values)! : Url.Page("/Index", null, values)!;
    }

    // ---- Needs attention ----

    public List<AttentionItem> Attention { get; } = [];

    private async Task LoadAttentionAsync(int id)
    {
        var unallocated = (await Get<BankImportService>().GetUnallocatedAsync(id)).Count;
        if (unallocated > 0)
            Attention.Add(new($"{unallocated:N0} bank line{(unallocated == 1 ? "" : "s")} to code", "/Accounting/Unallocated", unallocated > 20));

        var holdings = (await Get<HoldingsService>().GetHoldingsAsync(id, null, true)).Holdings;
        var dates = await dashboard.GetLastPriceDatesAsync(holdings.Select(h => (h.ExchangeCode, h.StockCode)));
        var stale = DashboardFigures.StalePrices(holdings, dates, Today);
        if (stale.Count > 0)
            Attention.Add(new($"Prices more than {DashboardFigures.StalePriceDays} days old: {Codes(stale)}", "/Prices/Manual"));
        var noCost = DashboardFigures.MissingCost(holdings);
        if (noCost.Count > 0)
            Attention.Add(new($"No cost recorded for {Codes(noCost)}, so gains and tax are wrong", "/Trades/Index", true));

        if (Region.IsAustralia && await Get<SalesTaxService>().GetSetupAsync(id) is { } gst
            && DashboardFigures.NextBas(Today, gst.ReportingMonths) is { } bas && bas.Due.DayNumber - Today.DayNumber <= 28)
            Attention.Add(new($"{gst.TaxName} return for {bas.From:d MMM} to {bas.To:d MMM yyyy} usually due {bas.Due:d MMM yyyy}", "/SalesTax/Index",
                bas.Due.DayNumber - Today.DayNumber <= 7));

        if (await Get<NavexaSyncService>().GetConnectionAsync(id) is { LastResult: { } result } && result.Contains("failed", StringComparison.OrdinalIgnoreCase))
            Attention.Add(new($"Navexa sync: {result}", "/Import/NavexaSync", true));

        foreach (var r in (await RentalRemindersAsync(id)).Where(r => r.Urgent).Take(3))
            Attention.Add(new(r.Text, "/Rentals/Index", true));

        var overdueInvoices = (await Get<InvoiceService>().GetInvoicesAsync(id)).Where(i => i.Balance > 0 && i.DueDate < Today).ToList();
        if (overdueInvoices.Count > 0)
            Attention.Add(new($"{overdueInvoices.Count:N0} overdue invoice{(overdueInvoices.Count == 1 ? "" : "s")} owed to you: {overdueInvoices.Sum(i => i.Balance):C}", "/Invoices/Aged"));
        var overdueBills = (await Get<BillService>().GetBillsAsync(id)).Where(b => !b.IsCredit && b.Balance > 0 && b.DueDate < Today).ToList();
        if (overdueBills.Count > 0)
            Attention.Add(new($"{overdueBills.Count:N0} overdue bill{(overdueBills.Count == 1 ? "" : "s")} to pay: {overdueBills.Sum(b => b.Balance):C}", "/Bills/Aged", true));
    }

    private static string Codes(IReadOnlyList<Holding> holdings) =>
        string.Join(", ", holdings.Take(5).Select(h => h.StockCode)) + (holdings.Count > 5 ? $" and {holdings.Count - 5} more" : "");

    // ---- Top holdings and allocation ----

    public IReadOnlyList<TopHolding> Top { get; private set; } = [];
    public IReadOnlyList<AllocationSlice> Allocation { get; private set; } = [];
    public bool BySector => string.Equals(Alloc, "sector", StringComparison.OrdinalIgnoreCase);

    private async Task LoadHoldingsAsync(int id)
    {
        var holdings = (await Get<HoldingsService>().GetHoldingsAsync(id, null, true)).Holdings;
        Top = DashboardFigures.Top(holdings);
        if (BySector)
        {
            var diversity = await Get<DiversityService>().GetAsync(id);
            Allocation = diversity.Sectors.Where(s => s.Value > 0).OrderByDescending(s => s.Value)
                .Select(s => new AllocationSlice(string.IsNullOrWhiteSpace(s.Sector) ? "Not set" : s.Sector, s.Value, s.Share)).ToList();
        }
        else
        {
            Value = await dashboard.GetValueAsync(id);
            Properties = await PropertiesAsync(id);
            Allocation = DashboardFigures.ByType(holdings, Value.TotalCashValue, PropertyEquity, Region.IsAustralia);
        }
    }

    public string AllocLink(string alloc, bool section) =>
        Url.Page("/Index", section ? "Section" : null, new { name = section ? "holdings" : null, period = Period, view = View, bm = Bm, alloc })!;

    // ---- Income ----

    public IReadOnlyList<IncomeMonth> IncomeMonths { get; private set; } = [];
    public IReadOnlyList<UpcomingIncome> Upcoming { get; private set; } = [];

    private async Task LoadIncomeAsync(int id)
    {
        var from = new DateTime(Today.Year, Today.Month, 1).AddMonths(-11);
        var received = await Get<PayoutService>().GetPayoutsAsync(id, from, Today.ToDateTime(TimeOnly.MinValue));
        IncomeMonths = DashboardFigures.IncomeMonths(received.Payouts, await dashboard.GetProjectedPayoutsAsync(id), Today);
        Upcoming = (await Get<IncomeCalendarService>().GetUpcomingAsync(id, Today)).Take(6).ToList();
    }

    // ---- Tax year so far (and SMSF members) ----

    public TaxWorkpaper? Workpaper { get; private set; }
    public PlanResult? IndividualEstimate { get; private set; }
    public (decimal Losses, int Holdings, DateOnly YearEnd)? Harvest { get; private set; }

    private async Task LoadTaxAsync(int id)
    {
        if (!Region.IsAustralia)
            return;
        var year = FinancialYear.EndYearFor(Today, Region.YearStart);
        Workpaper = await Get<TaxWorkpaperService>().BuildAsync(id, year);
        if (Workpaper is { } w && w.EntityType == ClientEntityTypes.Individual)
        {
            // The investments on their own: no salary or other deductions, which only Tax planning knows
            var investment = w.Reconciliation.TaxableIncome - w.Cgt.NetCapitalGain;
            var rental = w.Rental?.Net ?? 0;
            IndividualEstimate = TaxPlanner.Work(new PlanInputs(0, investment, rental, w.Cgt.NetCapitalGain, 0, 0,
                w.Income.FrankingCredits, w.Income.TfnWithheld + w.Ess.TfnWithheld + w.Data.Settings.PaygInstalments, w.Income.ForeignTax,
                w.Ess.UpfrontEligible, w.Ess.UpfrontNotEligible + w.Ess.Deferral + w.Ess.Pre2009), IndividualRates.For(year));
        }
        var holdings = (await Get<HoldingsService>().GetHoldingsAsync(id, null, true)).Holdings;
        Harvest = DashboardFigures.HarvestHint(holdings, Today, Region.YearStart);
    }

    // ---- Rental properties ----

    public int ActiveProperties { get; private set; }
    public int VacantProperties { get; private set; }
    public decimal Arrears { get; private set; }
    public decimal WeeklyRent { get; private set; }
    public IReadOnlyList<PropertyReminder> Reminders { get; private set; } = [];

    private async Task<IReadOnlyList<PropertyReminder>> RentalRemindersAsync(int id)
    {
        var properties = (await rentals.GetPropertiesAsync(id)).Where(p => p.Active).ToList();
        if (properties.Count == 0)
            return [];
        var b = await Get<PropertyService>().GetAsync(id);
        var current = b.Tenancies.Where(t => t.Current(Today)).ToList();
        ActiveProperties = properties.Count;
        VacantProperties = properties.Count(p => current.All(t => t.PropertyId != p.Id));
        var positions = current.Select(t => RentLedger.Position(t, b.Payments, Today)).ToList();
        Arrears = positions.Sum(p => p.Arrears);
        return PropertyReminders.Due(properties, b.Tenancies, b.Payments, b.Compliance, Today);
    }

    private async Task LoadPropertyAsync(int id)
    {
        Reminders = await RentalRemindersAsync(id);
        Properties = await PropertiesAsync(id);
    }

    // ---- Business books ----

    public bool HasBusiness { get; private set; }
    public decimal Receivable { get; private set; }
    public decimal ReceivableOverdue { get; private set; }
    public decimal Payable { get; private set; }
    public decimal PayableOverdue { get; private set; }

    private async Task LoadBusinessAsync(int id)
    {
        var invoices = await Get<InvoiceService>().GetInvoicesAsync(id);
        var bills = (await Get<BillService>().GetBillsAsync(id)).Where(b => !b.IsCredit).ToList();
        HasBusiness = invoices.Count > 0 || bills.Count > 0;
        Receivable = invoices.Sum(i => i.Balance);
        ReceivableOverdue = invoices.Where(i => i.DueDate < Today).Sum(i => i.Balance);
        Payable = bills.Sum(b => b.Balance);
        PayableOverdue = bills.Where(b => b.DueDate < Today).Sum(b => b.Balance);
    }

    // ---- Recent activity ----

    public sealed record ActivityRow(DateTime Date, string Kind, string Text, decimal? Amount, string Page, int? RouteId = null);

    public IReadOnlyList<ActivityRow> Activity { get; private set; } = [];

    private async Task LoadActivityAsync(int id)
    {
        var trades = (await Get<TradesService>().GetTradesAsync(id, null, false)).Take(6)
            .Select(t => new ActivityRow(t.Date, t.Type, $"{t.Type} {t.Quantity:#,0.########} {t.StockCode}", t.OrderTotal, "/Trades/Index"));
        var payouts = (await Get<PayoutService>().GetPayoutsAsync(id, Today.AddDays(-180).ToDateTime(TimeOnly.MinValue), null)).Payouts
            .Where(p => p.Date is not null)
            .OrderByDescending(p => p.Date).Take(6)
            .Select(p => new ActivityRow(p.Date!.Value, p.IsRental ? "Rent" : "Income", p.IsRental ? "Rental income" : $"Income from {p.StockCode}",
                p.Total, p.IsRental ? "/Payouts/Index" : "/Payouts/Edit", p.IsRental ? null : p.PayoutId));
        Activity = trades.Concat(payouts).OrderByDescending(a => a.Date).Take(8).ToList();
    }

    // ---- Clients (accountants with several portfolios) ----

    public sealed record ClientRow(PracticeClientYear Client, decimal? Value)
    {
        public int Outstanding => Client.Unallocated + Client.OpenQueries + Client.AnsweredQueries;
    }

    public const int ClientRows = 12;
    public IReadOnlyList<ClientRow> ClientList { get; private set; } = [];
    public int ClientCount { get; private set; }
    public int ClientYear { get; private set; }

    private async Task LoadClientsAsync()
    {
        var accessible = await current.GetAccessibleAsync();
        if (accessible.Count <= 1)
            return;
        ClientYear = FinancialYear.LastCompleted(Today, Region.YearStart);
        var rows = await Get<PracticeDashboardService>().GetAsync(CurrentPortfolio.UserId(User), accessible.Select(p => p.PortfolioId).ToHashSet(), ClientYear);
        ClientCount = rows.Count;
        // Most to do first; values only for the rows shown, one small query each
        var shown = rows.OrderByDescending(r => r.Unallocated + r.OpenQueries + r.AnsweredQueries)
            .ThenBy(r => r.Client.PortfolioName, StringComparer.CurrentCultureIgnoreCase)
            .Take(ClientRows).ToList();
        var list = new List<ClientRow>();
        foreach (var r in shown)
            list.Add(new ClientRow(r, (await dashboard.GetValueAsync(r.Client.PortfolioId)).TotalPortfolioValue));
        ClientList = list;
    }

    public static string Pct(double? value) => value is { } v ? (v * 100).ToString("0.00") + "%" : "–";

    public static string Pct(decimal? value) => value is { } v ? (v * 100).ToString("0.0") + "%" : "–";

    public static string Signed(decimal value, string format = "C") => (value > 0 ? "+" : "") + value.ToString(format);

    public static string Tone(decimal value) => value > 0 ? "text-success" : value < 0 ? "text-danger" : "";

    public static string Tone(double value) => value > 0 ? "text-success" : value < 0 ? "text-danger" : "";
}
