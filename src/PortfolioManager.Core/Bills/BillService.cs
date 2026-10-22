using System.Data;
using Dapper;
using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Data;
using PortfolioManager.Core.Invoicing;
using PortfolioManager.Core.SalesTax;

namespace PortfolioManager.Core.Bills;

/// <summary>
/// Suppliers, bills, supplier credits, payments and repeating bills (2026-10-21-25-bills.sql). Approving a bill and
/// paying it post journals through the bill procs, so Accounts Payable and the sales tax report always match the bills.
/// </summary>
public sealed class BillService(IDbConnectionFactory db, SalesTaxService salesTax)
{
    public async Task<IReadOnlyList<Supplier>> GetSuppliersAsync(int portfolioId)
    {
        await using var con = db.Create();
        try
        {
            var rows = await con.QueryAsync("""
                SELECT S.*, (SELECT COUNT(*) FROM tblBills B WHERE B.iSupplierID = S.iSupplierID) AS BillCount,
                       ISNULL((SELECT SUM(CASE WHEN B.strKind = 'SupplierCredit' THEN -1 ELSE 1 END
                                   * (B.dblTotal - ISNULL(P.Paid, 0)) * ISNULL(B.dblHomeTotal, B.dblTotal) / NULLIF(B.dblTotal, 0))
                               FROM tblBills B
                               LEFT JOIN (SELECT iBillID, SUM(dblAmount) AS Paid FROM tblBillPayments GROUP BY iBillID) P ON P.iBillID = B.iBillID
                               WHERE B.iSupplierID = S.iSupplierID AND B.strStatus = 'Approved'), 0) AS Owing
                FROM tblSuppliers S WHERE S.iUserID = @portfolioId ORDER BY S.strName
                """, new { portfolioId });
            return rows.Cast<IDictionary<string, object?>>().Select(MapSupplier).ToList();
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208)
        {
            return [];
        }
    }

    public static Supplier MapSupplier(IDictionary<string, object?> r) => new(
        Db.Int(r, "iSupplierID") ?? 0,
        Db.Str(r, "strName"),
        Optional(r, "strContactName"),
        Optional(r, "strEmail"),
        Optional(r, "strPhone"),
        Optional(r, "strAddress"),
        Optional(r, "strTaxNumber"),
        Optional(r, "strPaymentDetails"),
        Db.Int(r, "iDefaultAccountID"),
        Db.Int(r, "iDefaultTaxCodeID"),
        Db.Int(r, "iDueDays"),
        Db.Bool(r, "bIsActive") ?? true,
        Db.Int(r, "BillCount") ?? 0,
        decimal.Round(Db.Dec(r, "Owing") ?? 0, 2));

    public async Task<ProcResult> SaveSupplierAsync(int portfolioId, SupplierInput input)
    {
        if (input.Validate() is [var first, ..])
            return ProcResult.Failed(first);
        await using var con = db.Create();
        var row = await con.QuerySingleOrDefaultAsync<(string? Result, int? Id)>("uspSaveSupplier", new
        {
            iUserId = portfolioId,
            iSupplierID = input.SupplierId,
            strName = input.Name?.Trim() ?? "",
            strContactName = input.ContactName,
            strEmail = input.Email,
            strPhone = input.Phone,
            strAddress = input.Address,
            strTaxNumber = input.TaxNumber,
            strPaymentDetails = input.PaymentDetails,
            iDefaultAccountID = input.DefaultAccountId,
            iDefaultTaxCodeID = input.DefaultTaxCodeId,
            iDueDays = input.DueDays,
            bIsActive = input.IsActive,
        }, commandType: CommandType.StoredProcedure);
        return ProcResult.From(row.Result, "Success", row.Id);
    }

    public async Task<ProcResult> DeleteSupplierAsync(int portfolioId, int supplierId)
    {
        await using var con = db.Create();
        return ProcResult.From(await con.ExecuteScalarAsync<string>("uspDeleteSupplier",
            new { iUserId = portfolioId, iSupplierID = supplierId }, commandType: CommandType.StoredProcedure), "Supplier Deleted");
    }

    public async Task<IReadOnlyList<BillSummary>> GetBillsAsync(int portfolioId)
    {
        await using var con = db.Create();
        try
        {
            var rows = await con.QueryAsync("""
                SELECT B.iBillID, B.strKind, B.strBillNumber, B.iSupplierID, S.strName AS SupplierName, B.dtmIssueDate, B.dtmDueDate,
                       B.strReference, B.strStatus, B.dblTotal, B.strCurrency, B.dblHomeTotal,
                       ISNULL((SELECT SUM(P.dblAmount) FROM tblBillPayments P WHERE P.iBillID = B.iBillID), 0) AS AmountPaid
                FROM tblBills B JOIN tblSuppliers S ON S.iSupplierID = B.iSupplierID
                WHERE B.iUserID = @portfolioId
                ORDER BY B.dtmIssueDate DESC, B.iBillID DESC
                """, new { portfolioId });
            return rows.Cast<IDictionary<string, object?>>().Select(MapSummary).ToList();
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208)
        {
            return [];
        }
    }

    public static BillSummary MapSummary(IDictionary<string, object?> r) => new(
        Db.Int(r, "iBillID") ?? 0,
        Db.Str(r, "strKind") is { Length: > 0 } k ? k : BillKinds.Bill,
        Optional(r, "strBillNumber"),
        Db.Int(r, "iSupplierID") ?? 0,
        Db.Str(r, "SupplierName"),
        Db.Date(r, "dtmIssueDate") ?? default,
        Db.Date(r, "dtmDueDate") ?? default,
        Optional(r, "strReference"),
        Db.Str(r, "strStatus"),
        Db.Dec(r, "dblTotal") ?? 0,
        Db.Dec(r, "AmountPaid") ?? 0,
        Optional(r, "strCurrency"),
        Db.Dec(r, "dblHomeTotal"));

    /// <summary>The bill, its lines and payments; null unless it belongs to the portfolio.</summary>
    public async Task<BillDetail?> GetBillAsync(int portfolioId, int billId)
    {
        await using var con = db.Create();
        await using var grid = await con.QueryMultipleAsync("""
            SELECT B.*, S.strName AS SupplierName FROM tblBills B JOIN tblSuppliers S ON S.iSupplierID = B.iSupplierID
            WHERE B.iBillID = @billId AND B.iUserID = @portfolioId;
            SELECT L.*, T.strCode AS TaxCode FROM tblBillLines L JOIN tblBills B ON B.iBillID = L.iBillID
            LEFT JOIN tblTaxCodes T ON T.iTaxCodeID = L.iTaxCodeID
            WHERE L.iBillID = @billId AND B.iUserID = @portfolioId ORDER BY L.iLineNumber;
            SELECT P.*, A.strAccountName FROM tblBillPayments P JOIN tblBills B ON B.iBillID = P.iBillID
            LEFT JOIN tblChartOfAccounts A ON A.iAccountID = P.iAccountID
            WHERE P.iBillID = @billId AND B.iUserID = @portfolioId ORDER BY P.dtmPaymentDate, P.iPaymentID;
            """, new { portfolioId, billId });
        var header = (await grid.ReadAsync()).Cast<IDictionary<string, object?>>().FirstOrDefault();
        var lines = (await grid.ReadAsync()).Cast<IDictionary<string, object?>>().ToList();
        var payments = (await grid.ReadAsync()).Cast<IDictionary<string, object?>>().ToList();
        return MapDetail(header, lines, payments);
    }

    public static BillDetail? MapDetail(IDictionary<string, object?>? h, IEnumerable<IDictionary<string, object?>> lines, IEnumerable<IDictionary<string, object?>> payments)
    {
        if (h is null || Db.Int(h, "iBillID") is not { } id)
            return null;
        return new BillDetail(
            id,
            Db.Str(h, "strKind") is { Length: > 0 } k ? k : BillKinds.Bill,
            Optional(h, "strBillNumber"),
            Db.Int(h, "iSupplierID") ?? 0,
            Db.Str(h, "SupplierName"),
            Db.Date(h, "dtmIssueDate") ?? default,
            Db.Date(h, "dtmDueDate") ?? default,
            Optional(h, "strReference"),
            Optional(h, "strNotes"),
            Db.Bool(h, "bAmountsIncludeTax") ?? false,
            Db.Str(h, "strStatus"),
            Db.Dec(h, "dblNet") ?? 0,
            Db.Dec(h, "dblTax") ?? 0,
            Db.Dec(h, "dblTotal") ?? 0,
            Optional(h, "strCurrency"),
            Db.Dec(h, "dblExchangeRate"),
            Db.Dec(h, "dblHomeTotal"),
            Db.Int(h, "iPurchaseOrderID"),
            Db.Int(h, "iJournalID"),
            lines.Select(l => new InvoiceLine(
                    Db.Int(l, "iBillLineID") ?? 0,
                    Db.Str(l, "strDescription"),
                    Db.Dec(l, "dblQuantity") ?? 0,
                    Db.Dec(l, "dblUnitPrice") ?? 0,
                    Db.Int(l, "iAccountID") ?? 0,
                    Db.Int(l, "iTaxCodeID"),
                    Optional(l, "TaxCode"),
                    Db.Dec(l, "dblNet") ?? 0,
                    Db.Dec(l, "dblTax") ?? 0,
                    Db.Dec(l, "dblTotal") ?? 0,
                    Db.Int(l, "iItemID"),
                    Db.Int(l, "iTrackingOption1ID"),
                    Db.Int(l, "iTrackingOption2ID")))
                .ToList(),
            payments.Select(p => new BillPayment(
                    Db.Int(p, "iPaymentID") ?? 0,
                    Db.Date(p, "dtmPaymentDate") ?? default,
                    Db.Dec(p, "dblAmount") ?? 0,
                    Db.Int(p, "iAccountID") ?? 0,
                    Db.Str(p, "strAccountName"),
                    Optional(p, "strReference"),
                    Db.Int(p, "iJournalID"),
                    Db.Int(p, "iCreditID"),
                    Db.Dec(p, "dblExchangeRate"),
                    Db.Dec(p, "dblHomeAmount")))
                .ToList());
    }

    /// <summary>
    /// Saves a new or existing bill. With approve, a draft becomes approved and is posted; an approved bill is reposted
    /// whenever it is saved. <paramref name="accounts"/> are the accounts a line may go to.
    /// </summary>
    public async Task<ProcResult> SaveBillAsync(int portfolioId, int? billId, BillInput input, bool approve, IReadOnlySet<int> accounts)
    {
        input.NormaliseCurrency(Regions.RegionSettings.Current.Currency);
        var suppliers = (await GetSuppliersAsync(portfolioId)).Select(s => s.SupplierId).ToHashSet();
        var taxCodes = (await salesTax.GetCodesAsync(portfolioId)).ToDictionary(c => c.TaxCodeId);
        if (input.Validate(suppliers, accounts, taxCodes) is [var first, ..])
            return ProcResult.Failed(first);

        await using var con = db.Create();
        var row = await con.QuerySingleOrDefaultAsync<(string? Result, int? iBillID)>("uspSaveBill",
            SaveParameters(portfolioId, billId, input, approve, taxCodes), commandType: CommandType.StoredProcedure);
        return ProcResult.From(row.Result, "Success", row.iBillID);
    }

    public static object SaveParameters(int portfolioId, int? billId, BillInput input, bool approve, IReadOnlyDictionary<int, TaxCode> taxCodes) => new
    {
        iUserId = portfolioId,
        iBillID = billId,
        iSupplierID = input.SupplierId,
        strKind = input.Kind,
        strBillNumber = input.Number,
        dtIssueDate = input.IssueDate.Date,
        dtDueDate = input.DueDate.Date,
        strReference = input.Reference,
        strNotes = input.Notes,
        bAmountsIncludeTax = input.AmountsIncludeTax,
        bApprove = approve,
        strCurrency = input.Currency,
        dblExchangeRate = input.Currency is null ? null : input.ExchangeRate,
        iPurchaseOrderID = input.PurchaseOrderId,
        xmlLines = input.LinesXml(taxCodes),
    };

    /// <summary>Deletes a draft, or voids an approved bill with no payments (its journal is removed).</summary>
    public async Task<ProcResult> VoidBillAsync(int portfolioId, int billId)
    {
        await using var con = db.Create();
        return ProcResult.From(await con.ExecuteScalarAsync<string>("uspVoidBill",
            new { iUserId = portfolioId, iBillID = billId }, commandType: CommandType.StoredProcedure), "Bill");
    }

    /// <summary>Records money paid and posts it from Accounts Payable to the bank account.</summary>
    public async Task<ProcResult> AddPaymentAsync(int portfolioId, int billId, InvoicePaymentInput input)
    {
        if (input.Date == default)
            return ProcResult.Failed("Enter the date it was paid.");
        if (input.Amount is not > 0)
            return ProcResult.Failed("Enter the amount paid.");
        await using var con = db.Create();
        var row = await con.QuerySingleOrDefaultAsync<(string? Result, int? iPaymentID)>("uspAddBillPayment", new
        {
            iUserId = portfolioId,
            iBillID = billId,
            dtDate = input.Date.Date,
            dblAmount = input.Amount,
            iAccountID = input.AccountId,
            strReference = input.Reference,
            dblExchangeRate = input.ExchangeRate,
        }, commandType: CommandType.StoredProcedure);
        return ProcResult.From(row.Result, "Success", row.iPaymentID);
    }

    public async Task<ProcResult> DeletePaymentAsync(int portfolioId, int paymentId) =>
        await ResultAsync("uspDeleteBillPayment", new { iUserId = portfolioId, iPaymentID = paymentId }, "Payment Deleted");

    public async Task<ProcResult> ApplyCreditAsync(int portfolioId, int creditId, int billId, DateOnly date, decimal amount) =>
        await ResultAsync("uspApplySupplierCredit",
            new { iUserId = portfolioId, iCreditID = creditId, iBillID = billId, dtDate = date.ToDateTime(TimeOnly.MinValue), dblAmount = amount });

    // Repeating bills

    private sealed record RepeatingRow(int Id, int SupplierId, string SupplierName, string Name, string Frequency, DateTime Start, DateTime? End,
        int PostedCount, int DueDays, bool Approve, bool AmountsIncludeTax, string? Reference, string? Notes, string LinesXml, bool Active, DateTime? LastCreated);

    public async Task<IReadOnlyList<RepeatingBill>> GetRepeatingAsync(int portfolioId)
    {
        await using var con = db.Create();
        try
        {
            var rows = await con.QueryAsync<RepeatingRow>("""
                SELECT R.iRepeatingBillID AS Id, R.iSupplierID AS SupplierId, S.strName AS SupplierName, R.strName AS Name, R.strFrequency AS Frequency,
                       R.dtmStart AS Start, R.dtmEnd AS [End], R.iPostedCount AS PostedCount, R.iDueDays AS DueDays, R.bApprove AS Approve,
                       R.bAmountsIncludeTax AS AmountsIncludeTax, R.strReference AS Reference, R.strNotes AS Notes, R.strLinesXml AS LinesXml,
                       R.bActive AS Active, R.dtmLastCreated AS LastCreated
                FROM tblRepeatingBills R JOIN tblSuppliers S ON S.iSupplierID = R.iSupplierID
                WHERE R.iUserID = @portfolioId ORDER BY R.strName
                """, new { portfolioId });
            return rows.Select(r => new RepeatingBill(r.Id, r.SupplierId, r.SupplierName, r.Name, r.Frequency, DateOnly.FromDateTime(r.Start),
                r.End is { } e ? DateOnly.FromDateTime(e) : null, r.PostedCount, r.DueDays, r.Approve, r.AmountsIncludeTax, r.Reference, r.Notes,
                RepeatingInvoice.ParseLines(r.LinesXml), r.Active, r.LastCreated is { } l ? DateOnly.FromDateTime(l) : null)).ToList();
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208)
        {
            return [];
        }
    }

    /// <summary>Keeps a bill as a repeating one from <paramref name="start"/>. Nothing is created until it is due.</summary>
    public async Task<ProcResult> SaveRepeatingAsync(int portfolioId, int id, BillInput input, DateOnly start, RepeatingInvoiceInput repeat)
    {
        if (!RepeatFrequencies.All.Contains(repeat.Frequency))
            return ProcResult.Failed("Choose how often it repeats.");
        await using var con = db.Create();
        var row = await con.QuerySingleAsync<(string Result, int? Id)>("uspSaveRepeatingBill", new
        {
            iUserId = portfolioId, iRepeatingBillID = id, iSupplierID = input.SupplierId,
            strName = string.IsNullOrWhiteSpace(repeat.Name) ? $"Every {RepeatFrequencies.Label(repeat.Frequency).ToLowerInvariant()}" : repeat.Name.Trim(),
            strFrequency = repeat.Frequency, dtmStart = start.ToDateTime(TimeOnly.MinValue), dtmEnd = repeat.End?.Date,
            iDueDays = Math.Clamp((input.DueDate.Date - input.IssueDate.Date).Days, 0, 365), bApprove = repeat.Approve,
            bAmountsIncludeTax = input.AmountsIncludeTax, strReference = input.Reference, strNotes = input.Notes,
            strLinesXml = RepeatingInvoice.LinesXml(input.Lines),
        }, commandType: CommandType.StoredProcedure);
        return ProcResult.From(row.Result, "Success", row.Id);
    }

    public async Task<ProcResult> SetRepeatingActiveAsync(int portfolioId, int id, bool active) =>
        await ResultAsync("uspSetRepeatingBillActive", new { iUserId = portfolioId, iRepeatingBillID = id, bActive = active });

    public async Task<ProcResult> DeleteRepeatingAsync(int portfolioId, int id) =>
        await ResultAsync("uspDeleteRepeatingBill", new { iUserId = portfolioId, iRepeatingBillID = id });

    /// <summary>Creates every repeating bill due up to <paramref name="asAt"/>, moving the count on before each one.</summary>
    public async Task<(int Created, IReadOnlyList<string> Errors)> CreateDueAsync(int portfolioId, DateOnly asAt, IReadOnlySet<int> accounts)
    {
        var created = 0;
        var errors = new List<string>();
        foreach (var r in await GetRepeatingAsync(portfolioId))
        {
            var count = r.PostedCount;
            foreach (var date in r.DueDates(asAt))
            {
                var mark = await ResultAsync("uspMarkRepeatingBill",
                    new { iUserId = portfolioId, iRepeatingBillID = r.Id, iPostedCount = count, iBy = 1, dtmCreated = date.ToDateTime(TimeOnly.MinValue) });
                if (!mark.Ok)
                {
                    errors.Add($"{r.Name}: {mark.Error}");
                    break;
                }
                ProcResult saved;
                try
                {
                    saved = await SaveBillAsync(portfolioId, null, r.ToInput(date), r.Approve, accounts);
                }
                catch (Exception ex)
                {
                    saved = ProcResult.Failed(LockedPeriodService.IsLockError(ex) ? "that date is in a locked period." : ex.Message);
                }
                if (!saved.Ok)
                {
                    await ResultAsync("uspMarkRepeatingBill",
                        new { iUserId = portfolioId, iRepeatingBillID = r.Id, iPostedCount = count + 1, iBy = -1, dtmCreated = (DateTime?)null });
                    errors.Add($"{r.Name} on {date:d}: {saved.Error}");
                    break;
                }
                count++;
                created++;
            }
        }
        return (created, errors);
    }

    private async Task<ProcResult> ResultAsync(string proc, object args, string success = "Success")
    {
        await using var con = db.Create();
        return ProcResult.From(await con.ExecuteScalarAsync<string>(proc, args, commandType: CommandType.StoredProcedure), success);
    }

    private static string? Optional(IDictionary<string, object?> row, string column) =>
        Db.Str(row, column) is { Length: > 0 } s ? s : null;
}
