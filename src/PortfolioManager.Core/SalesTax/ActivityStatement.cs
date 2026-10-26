using System.Data;
using Dapper;
using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Data;

namespace PortfolioManager.Core.SalesTax;

public static class PaygMethods
{
    /// <summary>No PAYG instalments; option 1, the amount the ATO works out (T7); option 2, instalment income times the ATO's rate (T1 x T2).</summary>
    public const string None = "None", Amount = "Amount", Rate = "Rate";
    public static readonly IReadOnlyList<string> All = [None, Amount, Rate];
}

/// <summary>How the portfolio's BAS is worked out (tblBasSettings). Accrual basis and no instalments until saved.</summary>
public sealed record BasSettings(bool CashBasis, string PaygMethod, decimal? PaygAmount, decimal? PaygRate)
{
    public static readonly BasSettings Default = new(false, PaygMethods.None, null, null);
}

/// <summary>A label the user can type on the activity statement, where it goes and whether it is owed or a credit.</summary>
public sealed record BasLabelInfo(string Label, string Name, string Section, string? Help = null);

/// <summary>One line of the statement: entered, worked out from the ledger, or added up from other labels.</summary>
public sealed record BasLine(string Label, string Name, string Section, decimal? Amount, string Source, string? Help = null);

public sealed record ActivityStatement(IReadOnlyList<BasLine> Lines, decimal Owed, decimal Credits, string? Warning)
{
    /// <summary>Positive: payable to the ATO. Negative: a refund.</summary>
    public decimal Net => Owed - Credits;

    public decimal? Amount(string label) => Lines.FirstOrDefault(l => l.Label == label)?.Amount;
}

/// <summary>
/// The rest of the BAS around the GST boxes: PAYG withholding (W labels), PAYG instalments (T labels), FBT instalments (F),
/// wine equalisation tax (1C, 1D), luxury car tax (1E, 1F) and fuel tax credits (7C, 7D), then the summary (8A, 8B, 9).
/// The GST labels come from the tax codes; the rest are typed in for the period, except T1, which is suggested from the
/// ledger's ordinary income, and the totals.
/// </summary>
public static class ActivityStatementBuilder
{
    public static readonly IReadOnlyList<BasLabelInfo> Entered =
    [
        new("W1", "Total salary, wages and other payments", "PAYG tax withheld"),
        new("W2", "Amounts withheld from payments shown at W1", "PAYG tax withheld"),
        new("W4", "Amounts withheld where no ABN was quoted", "PAYG tax withheld"),
        new("W3", "Other amounts withheld (excluding W2 and W4)", "PAYG tax withheld"),
        new("T1", "PAYG instalment income", "PAYG income tax instalment",
            "Suggested from the ledger: income for the period, less capital gains, GST and franking credits. Type over it if it differs."),
        new("T9", "Varied amount for the quarter", "PAYG income tax instalment", "Only when varying the instalment; also give the reason at T4."),
        new("T4", "Reason code for the variation", "PAYG income tax instalment"),
        new("F1", "FBT instalment", "Fringe benefits tax instalment"),
        new("1C", "Wine equalisation tax payable", "Other taxes"),
        new("1D", "Wine equalisation tax refundable", "Other taxes"),
        new("1E", "Luxury car tax payable", "Other taxes"),
        new("1F", "Luxury car tax refundable", "Other taxes"),
        new("7C", "Fuel tax credits: litres", "Fuel tax credits", "Litres only; not counted in the totals."),
        new("7D", "Fuel tax credit", "Fuel tax credits"),
    ];

    public static bool IsEntered(string label) => Entered.Any(e => e.Label == label);

    public static ActivityStatement Build(SalesTaxReport gst, BasSettings settings, IReadOnlyDictionary<string, decimal> entered, decimal suggestedT1)
    {
        var lines = new List<BasLine>();
        decimal? Typed(string label) => entered.TryGetValue(label, out var v) ? v : null;
        void Add(string label, decimal? amount, string source, string? name = null, string? section = null)
        {
            var info = Entered.FirstOrDefault(e => e.Label == label);
            lines.Add(new BasLine(label, name ?? info?.Name ?? label, section ?? info?.Section ?? "", amount, source, info?.Help));
        }

        // GST, from the tax codes' boxes
        foreach (var b in gst.Boxes)
            Add(b.Box, Math.Round(b.Amount, 0, MidpointRounding.ToZero), "Tax codes", BoxName(b.Box), "Goods and services tax");
        var gstPayable = Math.Round(gst.Boxes.Where(b => b.Box == "1A").Sum(b => b.Amount), 0, MidpointRounding.ToZero);
        var gstCredit = Math.Round(gst.Boxes.Where(b => b.Box == "1B").Sum(b => b.Amount), 0, MidpointRounding.ToZero);

        // PAYG withholding
        foreach (var w in new[] { "W1", "W2", "W4", "W3" })
            Add(w, Typed(w), "Entered");
        var w5 = (Typed("W2") ?? 0) + (Typed("W4") ?? 0) + (Typed("W3") ?? 0);
        Add("W5", w5, "W2 + W4 + W3", "Total amounts withheld", "PAYG tax withheld");

        // PAYG instalments
        decimal instalment = 0;
        string? warning = null;
        if (Typed("T9") is { } varied)
        {
            Add("T9", varied, "Entered");
            Add("T4", Typed("T4"), "Entered");
            if (Typed("T4") is null)
                warning = "A varied instalment at T9 needs a reason code at T4.";
            instalment = varied;
        }
        else if (settings.PaygMethod == PaygMethods.Amount)
        {
            instalment = settings.PaygAmount ?? 0;
            Add("T7", instalment, "Settings", "Instalment amount (from the ATO)", "PAYG income tax instalment");
        }
        else if (settings.PaygMethod == PaygMethods.Rate)
        {
            var t1 = Typed("T1") ?? Math.Max(0, Math.Round(suggestedT1, 0, MidpointRounding.ToZero));
            var rate = settings.PaygRate ?? 0;
            Add("T1", t1, Typed("T1") is null ? "Ledger" : "Entered");
            Add("T2", rate, "Settings", "Instalment rate (%)", "PAYG income tax instalment");
            instalment = Math.Round(t1 * rate / 100m, 0, MidpointRounding.ToZero);
            Add("T11", instalment, "T1 x T2", "Instalment (T1 x T2)", "PAYG income tax instalment");
        }
        Add("5A", instalment, "PAYG instalment", "PAYG income tax instalment", "Summary");

        // FBT, WET, LCT and fuel tax credits
        foreach (var l in new[] { "F1", "1C", "1D", "1E", "1F", "7C", "7D" })
            if (Typed(l) is { } v)
                Add(l, v, "Entered");

        var owed = gstPayable + (Typed("1C") ?? 0) + (Typed("1E") ?? 0) + w5 + instalment + (Typed("F1") ?? 0);
        var credits = gstCredit + (Typed("1D") ?? 0) + (Typed("1F") ?? 0) + (Typed("7D") ?? 0);
        Add("8A", owed, "1A + 1C + 1E + 4 + 5A + 6A", "Total owed to the ATO", "Summary");
        Add("8B", credits, "1B + 1D + 1F + 7D", "Total owed by the ATO", "Summary");
        Add("9", owed - credits, "8A - 8B", owed >= credits ? "Payment due" : "Refund due", "Summary");
        return new ActivityStatement(lines, owed, credits, warning);
    }

    /// <summary>BAS amounts are whole dollars, cents left off.</summary>
    public static string BoxName(string box) => box switch
    {
        "G1" => "Total sales",
        "G2" => "Export sales",
        "G3" => "Other GST-free sales",
        "G4" => "Input-taxed sales",
        "G10" => "Capital purchases",
        "G11" => "Non-capital purchases",
        "G13" => "Purchases for making input-taxed sales",
        "G14" => "Purchases without GST in the price",
        "G15" => "Estimated purchases for private use or not income tax deductible",
        "1A" => "GST on sales",
        "1B" => "GST on purchases",
        _ => box,
    };

    /// <summary>
    /// The cash-basis report lines: every taxed line except those of invoices and bills, which come in when they are paid
    /// instead (<paramref name="paidLines"/>, from uspGetSalesTaxCashLines).
    /// </summary>
    public static IReadOnlyList<SalesTaxLine> CashBasis(IEnumerable<SalesTaxLine> accrualLines, IEnumerable<SalesTaxLine> paidLines) =>
        [.. accrualLines.Where(l => l.Source is not ("Invoice" or "Bill")), .. paidLines];
}

/// <summary>BAS settings and entered labels (2026-10-26-11-bas-extras.sql).</summary>
public sealed class ActivityStatementService(IDbConnectionFactory db, SalesTaxService salesTax, LedgerReportService ledger)
{
    public async Task<BasSettings> GetSettingsAsync(int portfolioId)
    {
        await using var con = db.Create();
        try
        {
            var r = await con.QuerySingleOrDefaultAsync<(bool Cash, string Method, decimal? Amount, decimal? Rate)?>(
                "SELECT bCashBasis, strPaygMethod, dblPaygAmount, dblPaygRate FROM tblBasSettings WHERE iUserID = @portfolioId", new { portfolioId });
            return r is { } s ? new BasSettings(s.Cash, PaygMethods.All.Contains(s.Method) ? s.Method : PaygMethods.None, s.Amount, s.Rate) : BasSettings.Default;
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208)
        {
            return BasSettings.Default;
        }
    }

    public async Task<ProcResult> SaveSettingsAsync(int portfolioId, BasSettings s)
    {
        await using var con = db.Create();
        return ProcResult.From(await con.ExecuteScalarAsync<string>("uspSaveBasSettings", new
        {
            iUserID = portfolioId, bCashBasis = s.CashBasis, strPaygMethod = s.PaygMethod,
            dblPaygAmount = s.PaygMethod == PaygMethods.Amount ? s.PaygAmount : null,
            dblPaygRate = s.PaygMethod == PaygMethods.Rate ? s.PaygRate : null,
        }, commandType: CommandType.StoredProcedure), "Success");
    }

    public async Task<IReadOnlyDictionary<string, decimal>> GetLabelsAsync(int portfolioId, DateOnly from)
    {
        await using var con = db.Create();
        try
        {
            return (await con.QueryAsync<(string Label, decimal Amount)>(
                    "SELECT strLabel, dblAmount FROM tblBasLabels WHERE iUserID = @portfolioId AND dtmPeriodFrom = @from",
                    new { portfolioId, from = from.ToDateTime(TimeOnly.MinValue) }))
                .ToDictionary(r => r.Label, r => r.Amount, StringComparer.OrdinalIgnoreCase);
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208)
        {
            return new Dictionary<string, decimal>();
        }
    }

    public async Task<ProcResult> SaveLabelAsync(int portfolioId, DateOnly from, DateOnly to, string label, decimal? amount)
    {
        if (!ActivityStatementBuilder.IsEntered(label))
            return ProcResult.Failed("That label can't be typed in.");
        await using var con = db.Create();
        return ProcResult.From(await con.ExecuteScalarAsync<string>("uspSaveBasLabel", new
        {
            iUserID = portfolioId, dtFrom = from.ToDateTime(TimeOnly.MinValue), dtTo = to.ToDateTime(TimeOnly.MinValue), strLabel = label, dblAmount = amount,
        }, commandType: CommandType.StoredProcedure), "Success");
    }

    /// <summary>The GST report for the period on the portfolio's basis: accruals, or invoices and bills when paid.</summary>
    public async Task<SalesTaxReport> GetGstAsync(int portfolioId, string taxName, DateOnly from, DateOnly to, bool cashBasis)
    {
        var accrual = await salesTax.GetReportAsync(portfolioId, taxName, from, to);
        if (!cashBasis)
            return accrual;
        var codes = await salesTax.GetCodesAsync(portfolioId);
        await using var con = db.Create();
        IEnumerable<IDictionary<string, object?>> rows;
        try
        {
            rows = (await con.QueryAsync("uspGetSalesTaxCashLines", new
            {
                iUserId = portfolioId, dtFrom = from.ToDateTime(TimeOnly.MinValue), dtTo = to.ToDateTime(TimeOnly.MinValue),
            }, commandType: CommandType.StoredProcedure)).Cast<IDictionary<string, object?>>();
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 2812)
        {
            return accrual;
        }
        var lines = ActivityStatementBuilder.CashBasis(accrual.Lines, SalesTaxService.MapLines(rows, codes));
        return SalesTaxReport.Build(taxName, from, to, codes, lines);
    }

    /// <summary>
    /// A starting figure for T1: the period's revenue in the ledger (amounts are before GST, as posted) less realised capital
    /// gains and franking credits, which aren't instalment income.
    /// </summary>
    public async Task<decimal> SuggestInstalmentIncomeAsync(int portfolioId, DateOnly from, DateOnly to)
    {
        var pl = await ledger.ProfitLossAsync(portfolioId, from, to);
        return InstalmentIncome(pl.Lines.Where(l => l.RowType == "Item" && l.Section == "Revenue").Select(l => (l.Label, l.Amount ?? 0)));
    }

    public static decimal InstalmentIncome(IEnumerable<(string Name, decimal Amount)> revenue) =>
        revenue.Where(r => !r.Name.Contains("capital gain", StringComparison.OrdinalIgnoreCase)
                           && !r.Name.Contains("franking", StringComparison.OrdinalIgnoreCase)
                           && !r.Name.Contains("unrealised", StringComparison.OrdinalIgnoreCase)
                           && !r.Name.Contains("revaluation", StringComparison.OrdinalIgnoreCase))
            .Sum(r => r.Amount);
}
