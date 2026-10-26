using System.Data;
using System.Globalization;
using Dapper;
using PortfolioManager.Core.Data;

namespace PortfolioManager.Core.Payouts;

public sealed record Payout(
    int PayoutId,
    string StockExchangeCode,
    string StockCode,
    DateTime? Date,
    decimal Total,
    decimal Franked,
    decimal Unfranked,
    decimal FrankingCredit,
    decimal Interest,
    decimal TrustIncome,
    decimal TaxWithheld,
    decimal ForeignTax,
    decimal Other,
    decimal RentalIncome,
    decimal RentalExpenses,
    string Comment,
    bool Confirmed)
{
    public bool IsRental => RentalIncome != 0 || RentalExpenses != 0;
}

public sealed record PayoutTotals(
    decimal Total, decimal Franked, decimal Unfranked, decimal FrankingCredit, decimal Interest,
    decimal TrustIncome, decimal TaxWithheld, decimal ForeignTax, decimal Other);

public sealed record PayoutsSummary(IReadOnlyList<Payout> Payouts, PayoutTotals Total);

/// <summary>One payout row straight from tblPayouts, for editing and ownership checks.</summary>
public sealed class PayoutRecord
{
    public int PayoutId { get; set; }
    public string? StockExchangeCode { get; set; }
    public string? StockCode { get; set; }
    public DateTime? Date { get; set; }
    public double? Total { get; set; }
    public double? Net { get; set; }
    public double? Franked { get; set; }
    public double? Unfranked { get; set; }
    public double? Interest { get; set; }
    public double? TaxWithheld { get; set; }
    public double? ForeignTax { get; set; }
    public double? Other { get; set; }
    public double? FrankingCredit { get; set; }
    public double? TrustIncome { get; set; }
    public string? Comment { get; set; }
    public string? Confirmed { get; set; }
    public double? RentalIncome { get; set; }
    public double? RentalExpenses { get; set; }
    public int? ReinvestmentTradeId { get; set; }

    public bool IsRental => (RentalIncome ?? 0) != 0 || (RentalExpenses ?? 0) != 0;
}

/// <summary>A dividend, distribution or interest payment as entered on the Add/Edit payout pages.</summary>
public sealed class PayoutInput
{
    public string StockExchangeCode { get; set; } = "ASX";
    public string StockCode { get; set; } = "";
    public DateTime Date { get; set; } = DateTime.Today;
    public decimal Total { get; set; }
    public decimal? Net { get; set; }
    public decimal Franked { get; set; }
    public decimal Unfranked { get; set; }
    public decimal FrankingCredit { get; set; }
    public decimal Interest { get; set; }
    public decimal TrustIncome { get; set; }
    public decimal TaxWithheld { get; set; }
    public decimal ForeignTax { get; set; }
    public decimal Other { get; set; }
    public string? Comment { get; set; }
    public bool Confirmed { get; set; } = true;

    /// <summary>What the parts add up to: income less tax withheld. Should match the cash received.</summary>
    public decimal PartsTotal => Franked + Unfranked + Interest + TrustIncome + Other - TaxWithheld - ForeignTax;

    /// <summary>Everything, for a new payout.</summary>
    public IReadOnlyList<string> Validate() => [.. ValidateCode(), .. ValidateDetails(), .. ValidateAmounts()];

    public IReadOnlyList<string> ValidateCode()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(StockCode))
            errors.Add("Enter the stock code (or the bank account name for interest).");
        else if (StockCode.Trim().Length > 50)
            errors.Add("The stock code can be at most 50 characters.");
        if (string.IsNullOrWhiteSpace(StockExchangeCode))
            errors.Add("Enter the exchange (e.g. ASX).");
        else if (StockExchangeCode.Trim().Length > 50)
            errors.Add("The exchange can be at most 50 characters.");
        return errors;
    }

    /// <summary>The date and comment, which can always be edited.</summary>
    public IReadOnlyList<string> ValidateDetails()
    {
        var errors = new List<string>();
        if (Date.Year < 1900 || Date.Year > 2078)
            errors.Add("Enter a valid payment date.");
        if ((Comment?.Length ?? 0) > 500)
            errors.Add("The comment can be at most 500 characters.");
        return errors;
    }

    /// <summary>
    /// The amounts must add up: cash received = franked + unfranked + interest + trust income + other
    /// − tax withheld − foreign tax (to the cent). The franking credit isn't cash, so it isn't part of the sum.
    /// </summary>
    public IReadOnlyList<string> ValidateAmounts()
    {
        var errors = new List<string>();
        if (new[] { Total, Net ?? 0, Franked, Unfranked, FrankingCredit, Interest, TrustIncome, TaxWithheld, ForeignTax, Other }.Any(v => v < 0))
            errors.Add("Amounts can't be negative.");
        else if (Total <= 0)
            errors.Add("Cash received must be more than zero.");
        else if (Franked + Unfranked + Interest + TrustIncome + Other == 0)
            errors.Add("Split the cash received into franked, unfranked, interest, trust income or other amounts.");
        else if (Math.Abs(PartsTotal - Total) > 0.01m)
            errors.Add($"The amounts come to {PartsTotal:C} (income less tax withheld) but the cash received is {Total:C}. Check the figures.");
        return errors;
    }
}

/// <summary>Dividends, distributions and interest (tblPayouts), through the legacy payout procs.</summary>
public sealed class PayoutService(IDbConnectionFactory db)
{
    // smalldatetime can't go past 6 June 2079
    public static readonly DateTime EarliestDate = new(1900, 1, 1);
    public static readonly DateTime LatestDate = new(2079, 6, 1);

    public async Task<PayoutsSummary> GetPayoutsAsync(int portfolioId, DateTime? from, DateTime? to)
    {
        await using var con = db.Create();
        // The proc's column names contain spaces, so read rows as dictionaries
        var rows = await con.QueryAsync("uspGetPayouts",
            new { iUserId = portfolioId, dtmStartDate = (from ?? EarliestDate).Date, dtmEndDate = (to ?? LatestDate).Date },
            commandType: CommandType.StoredProcedure);
        return Map(rows.Cast<IDictionary<string, object?>>());
    }

    private static readonly string[] RentalIncomeColumns = ["Rental Income", "Other Rental Related Income"];

    private static readonly string[] RentalExpenseColumns =
    [
        "Adverising For Tenants", "Body Corporate Fees", "Borrowing Expenses", "Cleaning", "Council Rates",
        "Capital Allowances (Depreciation)", "Gardening/Lawn Mowing", "Insurance", "Interest on Loans", "Land Tax",
        "Legal Fees", "Pest Control", "Property Agent Fees / Commission", "Repairs & Maintenance",
        "Capital Works Special Build Write Off", "Stationary, Telephone and Postage", "Travel Expenses", "Water Charges",
        "Sundry Expenses", "Sundry Expenses 2", "Sundry Expenses 3", "Sundry Expenses 4", "Sundry Expenses 5",
        "Sundry Expenses 6", "Sundry Expenses 7", "Sundry Expenses 8", "Sundry Expenses 9", "Sundry Expenses 10",
    ];

    /// <summary>
    /// Turns uspGetPayouts rows into payouts (newest first) plus the proc's total row,
    /// which has no payout id.
    /// </summary>
    public static PayoutsSummary Map(IEnumerable<IDictionary<string, object?>> rows)
    {
        var payouts = new List<Payout>();
        PayoutTotals? total = null;

        foreach (var r in rows)
        {
            if (r["Payout ID"] is null or DBNull)
            {
                total = new PayoutTotals(
                    Dec(r, "Total Gross"), Dec(r, "Franked Amount"), Dec(r, "Unfranked Amount"), Dec(r, "Franking Credit"),
                    Dec(r, "Interest"), Dec(r, "Trust Income"), Dec(r, "Tax Withheld"), Dec(r, "Foreign Tax Withheld"),
                    Dec(r, "Other Amount"));
                continue;
            }

            payouts.Add(new Payout(
                Convert.ToInt32(r["Payout ID"]),
                Str(r, "Stock Exchange"),
                Str(r, "Stock Code"),
                ParseDate(r["Payout Date"]),
                Dec(r, "Total Gross"),
                Dec(r, "Franked Amount"),
                Dec(r, "Unfranked Amount"),
                Dec(r, "Franking Credit"),
                Dec(r, "Interest"),
                Dec(r, "Trust Income"),
                Dec(r, "Tax Withheld"),
                Dec(r, "Foreign Tax Withheld"),
                Dec(r, "Other Amount"),
                RentalIncomeColumns.Sum(c => Dec(r, c)),
                RentalExpenseColumns.Sum(c => Dec(r, c)),
                Str(r, "Comment"),
                string.Equals(Str(r, "Has Payout Been Confirmed?"), "Yes", StringComparison.OrdinalIgnoreCase)));
        }

        payouts.Sort((a, b) => b.Date == a.Date ? b.PayoutId.CompareTo(a.PayoutId) : Nullable.Compare(b.Date, a.Date));
        total ??= new PayoutTotals(
            payouts.Sum(p => p.Total), payouts.Sum(p => p.Franked), payouts.Sum(p => p.Unfranked),
            payouts.Sum(p => p.FrankingCredit), payouts.Sum(p => p.Interest), payouts.Sum(p => p.TrustIncome),
            payouts.Sum(p => p.TaxWithheld), payouts.Sum(p => p.ForeignTax), payouts.Sum(p => p.Other));
        return new PayoutsSummary(payouts, total);
    }

    /// <summary>The proc formats dates as text with SQL style 106, e.g. "15 Oct 2026".</summary>
    public static DateTime? ParseDate(object? value)
    {
        switch (value)
        {
            case DateTime d:
                return d;
            case string s when DateTime.TryParseExact(s.Trim(), ["dd MMM yyyy", "d MMM yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d):
                return d;
            case string s when DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d):
                return d;
            default:
                return null;
        }
    }

    /// <summary>The payout, only if it belongs to this portfolio; otherwise null.</summary>
    public async Task<PayoutRecord?> GetAsync(int portfolioId, int payoutId)
    {
        await using var con = db.Create();
        return await GetAsync(con, null, portfolioId, payoutId);
    }

    private static Task<PayoutRecord?> GetAsync(IDbConnection con, IDbTransaction? tx, int portfolioId, int payoutId) =>
        con.QuerySingleOrDefaultAsync<PayoutRecord>(
            """
            SELECT iPayoutID AS PayoutId, strStockExchangeCode AS StockExchangeCode, strStockCode AS StockCode,
                   dtmTransactionDate AS Date, dblTotal AS Total, dblNetAmount AS Net, dblFrankedAmount AS Franked,
                   dblUnFrankedAmount AS Unfranked, dblInterestAmount AS Interest, dblTFNWithholdingAmount AS TaxWithheld,
                   dblForeignIncomeTaxAmount AS ForeignTax, dblOtherAmount AS Other, dblFrankingCredit AS FrankingCredit,
                   dblTrustIncome AS TrustIncome, strComment AS Comment, strPayoutConfirmed AS Confirmed,
                   ISNULL(dblRentalIncomeAmount, 0) + ISNULL(dblOtherRentalRelatedIncomeAmount, 0) AS RentalIncome,
                   ISNULL(dblAdvertisingForTenantsExpenseAmount, 0) + ISNULL(dblBodyCorportateFeesExpenseAmount, 0)
                   + ISNULL(dblBorrowingExpensesAmount, 0) + ISNULL(dblCleaningExpenseAmount, 0) + ISNULL(dblCouncilRatesExpenseAmount, 0)
                   + ISNULL(dblCapitalAllowancesExpenseAmount, 0) + ISNULL(dblGardeningAndLawnMowingExpenseAmount, 0)
                   + ISNULL(dblInsuranceExpenseAmount, 0) + ISNULL(dblInterestOnLoansExpenseAmount, 0) + ISNULL(dblLandTaxExpenseAmount, 0)
                   + ISNULL(dblLegalFeesExpenseAmount, 0) + ISNULL(dblPestControlExpenseAmount, 0)
                   + ISNULL(dblPropertyAgentFeesAndCommissionExpenseAmount, 0) + ISNULL(dblRepairsAndMaintenanceExpenseAmount, 0)
                   + ISNULL(dblCapitalWorksSpecialBuildWriteOffExpenseAmount, 0) + ISNULL(dblStationaryTelephoneAndPostageExpenseAmount, 0)
                   + ISNULL(dblTravelExpenseAmount, 0) + ISNULL(dblWaterChargesExpenseAmount, 0)
                   + ISNULL(dblSundryRentalExpense1Amount, 0) + ISNULL(dblSundryRentalExpense2Amount, 0) + ISNULL(dblSundryRentalExpense3Amount, 0)
                   + ISNULL(dblSundryRentalExpense4Amount, 0) + ISNULL(dblSundryRentalExpense5Amount, 0) + ISNULL(dblSundryRentalExpense6Amount, 0)
                   + ISNULL(dblSundryRentalExpense7Amount, 0) + ISNULL(dblSundryRentalExpense8Amount, 0) + ISNULL(dblSundryRentalExpense9Amount, 0)
                   + ISNULL(dblSundryRentalExpense10Amount, 0) AS RentalExpenses,
                   iReinvestmentTradeID AS ReinvestmentTradeId
            FROM tblPayouts
            WHERE iPayoutID = @payoutId AND iUserID = @portfolioId
            """,
            new { portfolioId, payoutId }, tx);

    /// <summary>The ledger journals posted for this payout (none for single-entry portfolios).</summary>
    private static async Task<IReadOnlyList<int>> GetJournalIdsAsync(IDbConnection con, IDbTransaction? tx, int portfolioId, int payoutId) =>
        (await con.QueryAsync<int>(
            "SELECT iJournalID FROM tblJournal WHERE iUserID = @portfolioId AND strSourceModule = 'Payout' AND iSourceID = @payoutId",
            new { portfolioId, payoutId }, tx)).ToList();

    /// <summary>Adds a payout with uspAddPayout, which also posts the journal for double-entry portfolios.</summary>
    public async Task AddAsync(int portfolioId, PayoutInput p)
    {
        await using var con = db.Create();
        await con.ExecuteAsync("uspAddPayout", AddParameters(portfolioId, p), commandType: CommandType.StoredProcedure);
    }

    /// <summary>uspAddPayout's parameters for a payout entered on the form.</summary>
    internal static object AddParameters(int portfolioId, PayoutInput p) =>
        new
        {
            iUserId = portfolioId,
            strStockExchangeCode = p.StockExchangeCode.Trim().ToUpperInvariant(),
            strStockCode = p.StockCode.Trim().ToUpperInvariant(),
            dtmTransactionDate = p.Date.Date,
            dblTotal = (double)p.Total,
            dblNetAmount = (double)(p.Net ?? p.Total),
            dblFrankedAmount = (double)p.Franked,
            dblUnFrankedAmount = (double)p.Unfranked,
            dblInterestAmount = (double)p.Interest,
            dblTFNWithholdingAmount = (double)p.TaxWithheld,
            dblForeignIncomeTaxAmount = (double)p.ForeignTax,
            dblOtherAmount = (double)p.Other,
            // Null lets the proc describe the journal as "Payout <code>"
            strComment = string.IsNullOrWhiteSpace(p.Comment) ? null : p.Comment.Trim(),
            strPayoutConfirmed = p.Confirmed ? "Yes" : "No",
            dblFrankingCredit = (double)p.FrankingCredit,
            dblTrustIncome = (double)p.TrustIncome,
        };

    /// <summary>
    /// Updates a payout with uspUpdatePayout, which re-posts its ledger journal to match.
    /// A DRP payout keeps its code and amounts (they belong with its reinvestment trade);
    /// keepAmounts keeps the stored amounts in any case.
    /// Returns false if the payout doesn't belong to the portfolio.
    /// </summary>
    public async Task<bool> UpdateAsync(int portfolioId, int payoutId, PayoutInput p, bool keepAmounts = false)
    {
        await using var con = db.Create();
        await con.OpenAsync();
        await using var tx = await con.BeginTransactionAsync();

        var existing = await GetAsync(con, tx, portfolioId, payoutId);
        if (existing is null)
            return false;

        var drp = existing.ReinvestmentTradeId is not null;
        var locked = keepAmounts || drp;

        await con.ExecuteAsync("uspUpdatePayout", new
        {
            iUserId = portfolioId,
            iPayoutID = payoutId,
            dtmTransactionDate = p.Date.Date,
            dblTotal = locked ? existing.Total : (double)p.Total,
            dblNetAmount = locked ? existing.Net : (double)(p.Net ?? p.Total),
            dblFrankedAmount = locked ? existing.Franked : (double)p.Franked,
            dblUnFrankedAmount = locked ? existing.Unfranked : (double)p.Unfranked,
            dblInterestAmount = locked ? existing.Interest : (double)p.Interest,
            dblTFNWithholdingAmount = locked ? existing.TaxWithheld : (double)p.TaxWithheld,
            dblForeignIncomeTaxAmount = locked ? existing.ForeignTax : (double)p.ForeignTax,
            dblOtherAmount = locked ? existing.Other : (double)p.Other,
            strComment = string.IsNullOrWhiteSpace(p.Comment) ? null : p.Comment.Trim(),
            strPayoutConfirmed = p.Confirmed ? "Yes" : "No",
            // Null keeps the stored value
            strStockExchangeCode = drp ? null : p.StockExchangeCode.Trim().ToUpperInvariant(),
            strStockCode = drp ? null : p.StockCode.Trim().ToUpperInvariant(),
            dblFrankingCredit = locked ? null : (double?)p.FrankingCredit,
            dblTrustIncome = locked ? null : (double?)p.TrustIncome,
        }, tx, commandType: CommandType.StoredProcedure);

        await tx.CommitAsync();
        return true;
    }

    /// <summary>A rental payout's fields, only if it belongs to this portfolio; otherwise null.</summary>
    public async Task<RentalPayoutInput?> GetRentalAsync(int portfolioId, int payoutId)
    {
        await using var con = db.Create();
        // Column names come from the fixed list in RentalPayoutInput, never from input
        IDictionary<string, object?>? row = await con.QuerySingleOrDefaultAsync(
            "SELECT strStockExchangeCode, strStockCode, dtmTransactionDate, strComment, " +
            string.Join(", ", RentalPayoutInput.Fields.Select(f => f.Column)) +
            " FROM tblPayouts WHERE iPayoutID = @payoutId AND iUserID = @portfolioId",
            new { portfolioId, payoutId });
        return row is null ? null : RentalPayoutInput.FromRow(row);
    }

    /// <summary>Adds a rental payout with uspAddRentalPayout, which also posts the journal for double-entry portfolios.</summary>
    public async Task AddRentalAsync(int portfolioId, RentalPayoutInput r)
    {
        await using var con = db.Create();
        await con.ExecuteAsync("uspAddRentalPayout", new DynamicParameters(r.AddParameters(portfolioId)),
            commandType: CommandType.StoredProcedure);
    }

    /// <summary>
    /// Updates a rental payout with uspUpdateRentalPayout, which re-posts its journal.
    /// Returns false if the payout doesn't belong to the portfolio.
    /// </summary>
    public async Task<bool> UpdateRentalAsync(int portfolioId, int payoutId, RentalPayoutInput r)
    {
        await using var con = db.Create();
        await con.OpenAsync();
        await using var tx = await con.BeginTransactionAsync();

        if (await GetAsync(con, tx, portfolioId, payoutId) is null)
            return false;

        await con.ExecuteAsync("uspUpdateRentalPayout", new DynamicParameters(r.UpdateParameters(portfolioId, payoutId)),
            tx, commandType: CommandType.StoredProcedure);
        await tx.CommitAsync();
        return true;
    }

    /// <summary>
    /// Deletes a payout with uspDeletePayout, which also deletes its journal and, for a DRP payout,
    /// its reinvestment trade. uspDeleteJournal is still called for any journal found first, in case
    /// the database predates that change. Returns null if the payout doesn't belong to the portfolio.
    /// </summary>
    public async Task<ProcResult?> DeleteAsync(int portfolioId, int payoutId)
    {
        await using var con = db.Create();
        await con.OpenAsync();
        await using var tx = await con.BeginTransactionAsync();

        if (await GetAsync(con, tx, portfolioId, payoutId) is null)
            return null;

        var journals = await GetJournalIdsAsync(con, tx, portfolioId, payoutId);
        var result = ProcResult.From(
            await con.ExecuteScalarAsync<string>("uspDeletePayout", new { iUserId = portfolioId, iPayoutID = payoutId },
                tx, commandType: CommandType.StoredProcedure),
            "Payout Deleted");
        if (!result.Ok)
            return result;

        foreach (var journalId in journals)
        {
            await con.ExecuteAsync("uspDeleteJournal", new { iUserId = portfolioId, iJournalID = journalId },
                tx, commandType: CommandType.StoredProcedure);
        }

        await tx.CommitAsync();
        return result;
    }

    /// <summary>
    /// Records a DRP with uspAddDRPTransaction: the payout and the buy of the new units in one
    /// transaction, with balanced journals for double-entry portfolios.
    /// </summary>
    public async Task<ProcResult> AddDrpAsync(int portfolioId, DrpInput d)
    {
        await using var con = db.Create();
        var row = (IDictionary<string, object?>)await con.QuerySingleAsync("uspAddDRPTransaction", new
        {
            iUserId = portfolioId,
            strStockExchangeCode = d.StockExchangeCode.Trim().ToUpperInvariant(),
            strStockCode = d.StockCode.Trim().ToUpperInvariant(),
            dtmTransactionDate = d.Date.Date,
            dblTotalPayout = (double)d.Cash,
            dblFrankedAmount = (double)d.Franked,
            dblUnFrankedAmount = (double)d.Unfranked,
            dblFrankingCredit = (double)d.FrankingCredit,
            iQuantity = (double)d.Units,
            dblPricePerUnit = (double)d.Price,
            strComment = string.IsNullOrWhiteSpace(d.Comment) ? "Dividend Reinvestment (DRP)" : d.Comment.Trim(),
            dblTFNWithholdingAmount = (double)d.TaxWithheld,
        }, commandType: CommandType.StoredProcedure);
        var text = row.TryGetValue("Result", out var v) && v is not null and not DBNull ? Convert.ToString(v) : null;
        return ProcResult.From(text, "DRP Added");
    }

    /// <summary>The financial year a date falls in (1 July to 30 June in Australia; the portfolio's year start otherwise).</summary>
    public static (DateTime Start, DateTime End) FinancialYear(DateTime date)
    {
        var end = Tax.FinancialYear.EndYearFor(DateOnly.FromDateTime(date));
        return (Tax.FinancialYear.StartDate(end).ToDateTime(TimeOnly.MinValue), Tax.FinancialYear.EndDate(end).ToDateTime(TimeOnly.MinValue));
    }

    private static string Str(IDictionary<string, object?> row, string column) =>
        row.TryGetValue(column, out var v) && v is not null and not DBNull ? Convert.ToString(v)!.Trim() : "";

    private static decimal Dec(IDictionary<string, object?> row, string column) =>
        row.TryGetValue(column, out var v) && v is not null and not DBNull ? Convert.ToDecimal(v) : 0m;
}
