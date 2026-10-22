using System.Data;
using Dapper;
using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Data;
using PortfolioManager.Core.SalesTax;

namespace PortfolioManager.Core.Invoicing;

/// <summary>
/// Customers, invoices and payments. Approving an invoice and recording a payment post journals
/// through the invoice procs, so the ledger and the sales tax report always match the invoices.
/// </summary>
public sealed class InvoiceService(IDbConnectionFactory db, SalesTaxService salesTax)
{
    public async Task<InvoiceSettings> GetSettingsAsync(int portfolioId)
    {
        await using var con = db.Create();
        var row = (await con.QueryAsync("uspGetInvoiceSettings", new { iUserId = portfolioId },
            commandType: CommandType.StoredProcedure)).Cast<IDictionary<string, object?>>().FirstOrDefault();
        return row is null ? InvoiceSettings.Default : MapSettings(row);
    }

    public static InvoiceSettings MapSettings(IDictionary<string, object?> r) => new(
        Db.Str(r, "strNumberPrefix"),
        Db.Int(r, "iNextNumber") ?? 1,
        Db.Int(r, "iDueDays") ?? 14,
        Db.Bool(r, "bAmountsIncludeTax") ?? false,
        Db.Str(r, "strPaymentDetails") is { Length: > 0 } p ? p : null,
        Db.Str(r, "strFooter") is { Length: > 0 } f ? f : null);

    public async Task<ProcResult> SaveSettingsAsync(int portfolioId, InvoiceSettingsInput input)
    {
        await using var con = db.Create();
        var result = await con.ExecuteScalarAsync<string>("uspSaveInvoiceSettings", new
        {
            iUserId = portfolioId,
            strNumberPrefix = input.NumberPrefix?.Trim() ?? "",
            iNextNumber = input.NextNumber,
            iDueDays = input.DueDays,
            bAmountsIncludeTax = input.AmountsIncludeTax,
            strPaymentDetails = input.PaymentDetails,
            strFooter = input.Footer,
        }, commandType: CommandType.StoredProcedure);
        return ProcResult.From(result, "Success");
    }

    public async Task<IReadOnlyList<Customer>> GetCustomersAsync(int portfolioId)
    {
        await using var con = db.Create();
        var rows = await con.QueryAsync("uspGetCustomers", new { iUserId = portfolioId }, commandType: CommandType.StoredProcedure);
        return rows.Cast<IDictionary<string, object?>>().Select(MapCustomer).ToList();
    }

    public static Customer MapCustomer(IDictionary<string, object?> r) => new(
        Db.Int(r, "iCustomerID") ?? 0,
        Db.Str(r, "strName"),
        Optional(r, "strContactName"),
        Optional(r, "strEmail"),
        Optional(r, "strPhone"),
        Optional(r, "strAddress"),
        Optional(r, "strTaxNumber"),
        Db.Bool(r, "bIsActive") ?? true,
        Db.Int(r, "InvoiceCount") ?? 0,
        Db.Dec(r, "Outstanding") ?? 0);

    public static object CustomerParameters(int portfolioId, CustomerInput input) => new
    {
        iUserId = portfolioId,
        iCustomerID = input.CustomerId,
        strName = input.Name?.Trim() ?? "",
        strContactName = input.ContactName,
        strEmail = input.Email,
        strPhone = input.Phone,
        strAddress = input.Address,
        strTaxNumber = input.TaxNumber,
        bIsActive = input.IsActive,
    };

    public async Task<ProcResult> SaveCustomerAsync(int portfolioId, CustomerInput input)
    {
        if (input.Validate() is [var first, ..])
            return ProcResult.Failed(first);
        await using var con = db.Create();
        var row = await con.QuerySingleOrDefaultAsync<(string? Result, int? iCustomerID)>("uspSaveCustomer",
            CustomerParameters(portfolioId, input), commandType: CommandType.StoredProcedure);
        return ProcResult.From(row.Result, "Success", row.iCustomerID);
    }

    public async Task<ProcResult> DeleteCustomerAsync(int portfolioId, int customerId)
    {
        await using var con = db.Create();
        var result = await con.ExecuteScalarAsync<string>("uspDeleteCustomer",
            new { iUserId = portfolioId, iCustomerID = customerId }, commandType: CommandType.StoredProcedure);
        return ProcResult.From(result, "Customer Deleted");
    }

    public async Task<IReadOnlyList<InvoiceSummary>> GetInvoicesAsync(int portfolioId)
    {
        await using var con = db.Create();
        var rows = await con.QueryAsync("uspGetInvoices", new { iUserId = portfolioId }, commandType: CommandType.StoredProcedure);
        return rows.Cast<IDictionary<string, object?>>().Select(r => new InvoiceSummary(
                Db.Int(r, "iInvoiceID") ?? 0,
                Db.Str(r, "strInvoiceNumber"),
                Db.Int(r, "iCustomerID") ?? 0,
                Db.Str(r, "CustomerName"),
                Db.Date(r, "dtmIssueDate") ?? default,
                Db.Date(r, "dtmDueDate") ?? default,
                Optional(r, "strReference"),
                Db.Str(r, "strStatus"),
                Db.Dec(r, "dblNet") ?? 0,
                Db.Dec(r, "dblTax") ?? 0,
                Db.Dec(r, "dblTotal") ?? 0,
                Db.Dec(r, "AmountPaid") ?? 0,
                Optional(r, "strCurrency"),
                Db.Dec(r, "dblHomeTotal")))
            .ToList();
    }

    /// <summary>The invoice, its lines and payments; null unless it belongs to the portfolio.</summary>
    public async Task<InvoiceDetail?> GetInvoiceAsync(int portfolioId, int invoiceId)
    {
        await using var con = db.Create();
        await using var grid = await con.QueryMultipleAsync("uspGetInvoice",
            new { iUserId = portfolioId, iInvoiceID = invoiceId }, commandType: CommandType.StoredProcedure);
        var header = (await grid.ReadAsync()).Cast<IDictionary<string, object?>>().FirstOrDefault();
        var lines = (await grid.ReadAsync()).Cast<IDictionary<string, object?>>().ToList();
        var payments = (await grid.ReadAsync()).Cast<IDictionary<string, object?>>().ToList();
        return MapDetail(header, lines, payments);
    }

    public static InvoiceDetail? MapDetail(IDictionary<string, object?>? h, IEnumerable<IDictionary<string, object?>> lines, IEnumerable<IDictionary<string, object?>> payments)
    {
        if (h is null || Db.Int(h, "iInvoiceID") is not { } id)
            return null;
        var customerId = Db.Int(h, "iCustomerID") ?? 0;
        return new InvoiceDetail(
            id,
            Db.Str(h, "strInvoiceNumber"),
            customerId,
            Db.Date(h, "dtmIssueDate") ?? default,
            Db.Date(h, "dtmDueDate") ?? default,
            Optional(h, "strReference"),
            Optional(h, "strNotes"),
            Db.Bool(h, "bAmountsIncludeTax") ?? false,
            Db.Str(h, "strStatus"),
            Db.Dec(h, "dblNet") ?? 0,
            Db.Dec(h, "dblTax") ?? 0,
            Db.Dec(h, "dblTotal") ?? 0,
            Db.Int(h, "iJournalID"),
            new Customer(customerId, Db.Str(h, "CustomerName"), Optional(h, "CustomerContact"), Optional(h, "CustomerEmail"),
                null, Optional(h, "CustomerAddress"), Optional(h, "CustomerTaxNumber"), true),
            lines.Select(l => new InvoiceLine(
                    Db.Int(l, "iInvoiceLineID") ?? 0,
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
            payments.Select(p => new InvoicePayment(
                    Db.Int(p, "iPaymentID") ?? 0,
                    Db.Date(p, "dtmPaymentDate") ?? default,
                    Db.Dec(p, "dblAmount") ?? 0,
                    Db.Int(p, "iAccountID") ?? 0,
                    Db.Str(p, "strAccountName"),
                    Optional(p, "strReference"),
                    Db.Int(p, "iJournalID"),
                    Db.Dec(p, "dblExchangeRate"),
                    Db.Dec(p, "dblHomeAmount")))
                .ToList(),
            Optional(h, "strCurrency"),
            Db.Dec(h, "dblExchangeRate"),
            Db.Dec(h, "dblHomeTotal"));
    }

    /// <summary>
    /// Saves a new or existing invoice. With approve, a draft becomes approved and is posted to the
    /// ledger; an invoice that is already approved is reposted whenever it is saved.
    /// </summary>
    public async Task<ProcResult> SaveInvoiceAsync(int portfolioId, int? invoiceId, InvoiceInput input, bool approve,
        IReadOnlySet<int> incomeAccounts)
    {
        input.NormaliseCurrency(Regions.RegionSettings.Current.Currency);
        var customers = (await GetCustomersAsync(portfolioId)).Select(c => c.CustomerId).ToHashSet();
        var taxCodes = (await salesTax.GetCodesAsync(portfolioId)).ToDictionary(c => c.TaxCodeId);
        if (input.Validate(customers, incomeAccounts, taxCodes) is [var first, ..])
            return ProcResult.Failed(first);

        await using var con = db.Create();
        var row = await con.QuerySingleOrDefaultAsync<(string? Result, int? iInvoiceID)>("uspSaveInvoice",
            SaveParameters(portfolioId, invoiceId, input, approve, taxCodes), commandType: CommandType.StoredProcedure);
        return ProcResult.From(row.Result, "Success", row.iInvoiceID);
    }

    public static object SaveParameters(int portfolioId, int? invoiceId, InvoiceInput input, bool approve, IReadOnlyDictionary<int, TaxCode> taxCodes) => new
    {
        iUserId = portfolioId,
        iInvoiceID = invoiceId,
        iCustomerID = input.CustomerId,
        dtIssueDate = input.IssueDate.Date,
        dtDueDate = input.DueDate.Date,
        strReference = input.Reference,
        strNotes = input.Notes,
        bAmountsIncludeTax = input.AmountsIncludeTax,
        bApprove = approve,
        xmlLines = input.LinesXml(taxCodes),
        strCurrency = input.Currency,
        dblExchangeRate = input.Currency is null ? null : input.ExchangeRate,
    };

    /// <summary>Deletes a draft, or voids an approved invoice with no payments (its journal is removed).</summary>
    public async Task<ProcResult> VoidInvoiceAsync(int portfolioId, int invoiceId)
    {
        await using var con = db.Create();
        var result = await con.ExecuteScalarAsync<string>("uspVoidInvoice",
            new { iUserId = portfolioId, iInvoiceID = invoiceId }, commandType: CommandType.StoredProcedure);
        return ProcResult.From(result, "Invoice");
    }

    /// <summary>Records money received and posts it from Accounts Receivable to the bank account.</summary>
    public async Task<ProcResult> AddPaymentAsync(int portfolioId, int invoiceId, InvoicePaymentInput input)
    {
        if (input.Date == default)
            return ProcResult.Failed("Enter the date it was paid.");
        if (input.Amount is not > 0)
            return ProcResult.Failed("Enter the amount paid.");
        await using var con = db.Create();
        var row = await con.QuerySingleOrDefaultAsync<(string? Result, int? iPaymentID)>("uspAddInvoicePayment", new
        {
            iUserId = portfolioId,
            iInvoiceID = invoiceId,
            dtDate = input.Date.Date,
            dblAmount = input.Amount,
            iAccountID = input.AccountId,
            strReference = input.Reference,
            dblExchangeRate = input.ExchangeRate,
        }, commandType: CommandType.StoredProcedure);
        return ProcResult.From(row.Result, "Success", row.iPaymentID);
    }

    public async Task<ProcResult> DeletePaymentAsync(int portfolioId, int paymentId)
    {
        await using var con = db.Create();
        var result = await con.ExecuteScalarAsync<string>("uspDeleteInvoicePayment",
            new { iUserId = portfolioId, iPaymentID = paymentId }, commandType: CommandType.StoredProcedure);
        return ProcResult.From(result, "Payment Deleted");
    }

    private static string? Optional(IDictionary<string, object?> row, string column) =>
        Db.Str(row, column) is { Length: > 0 } s ? s : null;
}
