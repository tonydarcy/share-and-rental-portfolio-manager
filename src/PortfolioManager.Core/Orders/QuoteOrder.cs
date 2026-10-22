using System.Data;
using Dapper;
using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Bills;
using PortfolioManager.Core.Data;
using PortfolioManager.Core.Invoicing;
using PortfolioManager.Core.SalesTax;

namespace PortfolioManager.Core.Orders;

public static class QuoteOrderKinds
{
    public const string Quote = "Quote", PurchaseOrder = "PurchaseOrder";

    public static string Label(string kind) => kind == Quote ? "Quote" : "Purchase order";
}

public static class QuoteOrderStatus
{
    public const string Draft = "Draft", Sent = "Sent", Accepted = "Accepted", Declined = "Declined", Invoiced = "Invoiced", Billed = "Billed",
        Cancelled = "Cancelled";

    /// <summary>The statuses a person can pick for each kind (Invoiced and Billed come from converting).</summary>
    public static IReadOnlyList<string> Choices(string kind) =>
        kind == QuoteOrderKinds.Quote ? [Draft, Sent, Accepted, Declined, Cancelled] : [Draft, Sent, Cancelled];
}

/// <summary>A quote (to a customer) or a purchase order (to a supplier). Neither posts to the ledger.</summary>
public sealed record QuoteOrder(
    int Id,
    string Kind,
    string Number,
    int ContactId,
    string ContactName,
    DateOnly Date,
    DateOnly? Expiry,
    string? Title,
    string? Reference,
    string? Notes,
    bool AmountsIncludeTax,
    string Status,
    decimal Net,
    decimal Tax,
    decimal Total,
    string? Currency,
    decimal? ExchangeRate,
    int? ConvertedId,
    IReadOnlyList<InvoiceLine> Lines)
{
    public bool IsQuote => Kind == QuoteOrderKinds.Quote;
    public bool IsConverted => Status is QuoteOrderStatus.Invoiced or QuoteOrderStatus.Billed;
    public bool CanConvert => !IsConverted && Status is not (QuoteOrderStatus.Declined or QuoteOrderStatus.Cancelled);
    public string Title2 => QuoteOrderKinds.Label(Kind) + " " + Number;

    public bool IsExpired(DateOnly today) => IsQuote && Expiry is { } e && e < today && Status is QuoteOrderStatus.Draft or QuoteOrderStatus.Sent;

    /// <summary>The draft invoice a quote becomes, dated today, with its lines, currency and rate.</summary>
    public InvoiceInput ToInvoice(DateTime today, int dueDays) => new()
    {
        CustomerId = ContactId,
        IssueDate = today.Date,
        DueDate = today.Date.AddDays(dueDays),
        Reference = Reference ?? Number,
        Notes = Notes,
        AmountsIncludeTax = AmountsIncludeTax,
        Currency = Currency,
        ExchangeRate = ExchangeRate,
        Lines = [.. Lines.Select(InputLine)],
    };

    /// <summary>The draft bill a purchase order becomes, when the supplier's invoice arrives.</summary>
    public BillInput ToBill(DateTime billDate, int dueDays, string? supplierInvoiceNumber) => new()
    {
        SupplierId = ContactId,
        Number = string.IsNullOrWhiteSpace(supplierInvoiceNumber) ? null : supplierInvoiceNumber.Trim(),
        IssueDate = billDate.Date,
        DueDate = billDate.Date.AddDays(dueDays),
        Reference = Number,
        Notes = Notes,
        AmountsIncludeTax = AmountsIncludeTax,
        Currency = Currency,
        ExchangeRate = ExchangeRate,
        PurchaseOrderId = Id,
        Lines = [.. Lines.Select(InputLine)],
    };

    private static InvoiceLineInput InputLine(InvoiceLine l) => new()
    {
        Description = l.Description, Quantity = l.Quantity, UnitPrice = l.UnitPrice, AccountId = l.AccountId, TaxCodeId = l.TaxCodeId,
        ItemId = l.ItemId, Tracking1Id = l.Tracking1Id, Tracking2Id = l.Tracking2Id,
    };
}

/// <summary>The quote and purchase order form.</summary>
public sealed class QuoteOrderInput : DocumentInput
{
    public string Kind { get; set; } = QuoteOrderKinds.Quote;
    public int ContactId { get; set; }
    public DateTime Date { get; set; }
    public DateTime? Expiry { get; set; }
    public string? Title { get; set; }
    public string? Reference { get; set; }
    public string? Notes { get; set; }

    public static QuoteOrderInput New(string kind, DateTime today, int defaultAccountId, bool amountsIncludeTax) => new()
    {
        Kind = kind,
        Date = today.Date,
        Expiry = kind == QuoteOrderKinds.Quote ? today.Date.AddDays(30) : null,
        AmountsIncludeTax = amountsIncludeTax,
        Lines = [new InvoiceLineInput { Quantity = 1, AccountId = defaultAccountId }],
    };

    public static QuoteOrderInput From(QuoteOrder d) => new()
    {
        Kind = d.Kind,
        ContactId = d.ContactId,
        Date = d.Date.ToDateTime(TimeOnly.MinValue),
        Expiry = d.Expiry?.ToDateTime(TimeOnly.MinValue),
        Title = d.Title,
        Reference = d.Reference,
        Notes = d.Notes,
        AmountsIncludeTax = d.AmountsIncludeTax,
        Currency = d.Currency,
        ExchangeRate = d.ExchangeRate,
        Lines = [.. d.Lines.Select(l => new InvoiceLineInput
        {
            Description = l.Description, Quantity = l.Quantity, UnitPrice = l.UnitPrice, AccountId = l.AccountId, TaxCodeId = l.TaxCodeId,
            ItemId = l.ItemId, Tracking1Id = l.Tracking1Id, Tracking2Id = l.Tracking2Id,
        })],
    };

    public IReadOnlyList<string> Validate(IReadOnlySet<int> contacts, IReadOnlySet<int> accounts, IReadOnlyDictionary<int, TaxCode> taxCodes)
    {
        var errors = new List<string>();
        if (Kind is not (QuoteOrderKinds.Quote or QuoteOrderKinds.PurchaseOrder))
            errors.Add("Choose a quote or a purchase order.");
        else if (!contacts.Contains(ContactId))
            errors.Add(Kind == QuoteOrderKinds.Quote ? "Choose a customer." : "Choose a supplier.");
        if (Date == default)
            errors.Add("Enter the date.");
        else if (Expiry is { } e && e.Date < Date.Date)
            errors.Add(Kind == QuoteOrderKinds.Quote ? "The expiry date can't be before the quote date." : "The delivery date can't be before the order date.");
        if ((Title?.Trim().Length ?? 0) > 200)
            errors.Add("The title can be at most 200 characters.");
        ValidateLines(errors, accounts, taxCodes, "choose the account it goes to");
        if (errors.Count == 0 && Totals(taxCodes).Total <= 0)
            errors.Add("The total must be more than zero.");
        return errors;
    }
}

/// <summary>Quotes and purchase orders, and turning them into invoices and bills (2026-10-21-27-quotes-orders.sql).</summary>
public sealed class QuoteOrderService(IDbConnectionFactory db, SalesTaxService salesTax, InvoiceService invoices, BillService bills)
{
    public async Task<IReadOnlyList<QuoteOrder>> GetAsync(int portfolioId, string? kind = null, int? id = null)
    {
        await using var con = db.Create();
        try
        {
            var headers = (await con.QueryAsync("""
                SELECT D.*, ISNULL(C.strName, S.strName) AS ContactName
                FROM tblQuotesOrders D
                LEFT JOIN tblCustomers C ON C.iCustomerID = D.iCustomerID
                LEFT JOIN tblSuppliers S ON S.iSupplierID = D.iSupplierID
                WHERE D.iUserID = @portfolioId AND (@kind IS NULL OR D.strKind = @kind) AND (@id IS NULL OR D.iDocumentID = @id)
                ORDER BY D.dtmDate DESC, D.iDocumentID DESC
                """, new { portfolioId, kind, id })).Cast<IDictionary<string, object?>>().ToList();
            var lines = id is null
                ? []
                : (await con.QueryAsync("""
                    SELECT L.*, T.strCode AS TaxCode FROM tblQuoteOrderLines L
                    JOIN tblQuotesOrders D ON D.iDocumentID = L.iDocumentID
                    LEFT JOIN tblTaxCodes T ON T.iTaxCodeID = L.iTaxCodeID
                    WHERE D.iUserID = @portfolioId AND L.iDocumentID = @id ORDER BY L.iLineNumber
                    """, new { portfolioId, id })).Cast<IDictionary<string, object?>>().ToList();
            return headers.Select(h => Map(h, lines)).ToList();
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208)
        {
            return [];
        }
    }

    public async Task<QuoteOrder?> GetOneAsync(int portfolioId, int id) => (await GetAsync(portfolioId, null, id)).FirstOrDefault();

    public static QuoteOrder Map(IDictionary<string, object?> h, IEnumerable<IDictionary<string, object?>> lines)
    {
        var kind = Db.Str(h, "strKind");
        string? Optional(string column) => Db.Value(h, column) is null ? null : Db.Str(h, column);
        return new QuoteOrder(
            Db.Int(h, "iDocumentID") ?? 0,
            kind,
            Db.Str(h, "strNumber"),
            (kind == QuoteOrderKinds.Quote ? Db.Int(h, "iCustomerID") : Db.Int(h, "iSupplierID")) ?? 0,
            Db.Str(h, "ContactName"),
            Db.Date(h, "dtmDate") ?? default,
            Db.Date(h, "dtmExpiry"),
            Optional("strTitle"),
            Optional("strReference"),
            Optional("strNotes"),
            Db.Bool(h, "bAmountsIncludeTax") ?? false,
            Db.Str(h, "strStatus"),
            Db.Dec(h, "dblNet") ?? 0,
            Db.Dec(h, "dblTax") ?? 0,
            Db.Dec(h, "dblTotal") ?? 0,
            Optional("strCurrency")?.Trim(),
            Db.Dec(h, "dblExchangeRate"),
            Db.Int(h, "iConvertedID"),
            lines.Select(l => new InvoiceLine(
                Db.Int(l, "iLineID") ?? 0,
                Db.Str(l, "strDescription"),
                Db.Dec(l, "dblQuantity") ?? 0,
                Db.Dec(l, "dblUnitPrice") ?? 0,
                Db.Int(l, "iAccountID") ?? 0,
                Db.Int(l, "iTaxCodeID"),
                Db.Value(l, "TaxCode") is null ? null : Db.Str(l, "TaxCode"),
                Db.Dec(l, "dblNet") ?? 0,
                Db.Dec(l, "dblTax") ?? 0,
                Db.Dec(l, "dblTotal") ?? 0,
                Db.Int(l, "iItemID"),
                Db.Int(l, "iTrackingOption1ID"),
                Db.Int(l, "iTrackingOption2ID"))).ToList());
    }

    public async Task<ProcResult> SaveAsync(int portfolioId, int? id, QuoteOrderInput input, IReadOnlySet<int> contacts, IReadOnlySet<int> accounts)
    {
        input.NormaliseCurrency(Regions.RegionSettings.Current.Currency);
        var taxCodes = (await salesTax.GetCodesAsync(portfolioId)).ToDictionary(c => c.TaxCodeId);
        if (input.Validate(contacts, accounts, taxCodes) is [var first, ..])
            return ProcResult.Failed(first);
        await using var con = db.Create();
        var row = await con.QuerySingleAsync<(string Result, int? Id)>("uspSaveQuoteOrder", SaveParameters(portfolioId, id, input, taxCodes),
            commandType: CommandType.StoredProcedure);
        return ProcResult.From(row.Result, "Success", row.Id);
    }

    public static object SaveParameters(int portfolioId, int? id, QuoteOrderInput input, IReadOnlyDictionary<int, TaxCode> taxCodes) => new
    {
        iUserId = portfolioId,
        iDocumentID = id,
        strKind = input.Kind,
        iContactID = input.ContactId,
        dtDate = input.Date.Date,
        dtExpiry = input.Expiry?.Date,
        strTitle = input.Title?.Trim(),
        strReference = input.Reference?.Trim(),
        strNotes = input.Notes,
        bAmountsIncludeTax = input.AmountsIncludeTax,
        strCurrency = input.Currency,
        dblExchangeRate = input.Currency is null ? null : input.ExchangeRate,
        xmlLines = input.LinesXml(taxCodes),
    };

    public async Task<ProcResult> SetStatusAsync(int portfolioId, int id, string status)
    {
        await using var con = db.Create();
        return ProcResult.From(await con.ExecuteScalarAsync<string>("uspSetQuoteOrderStatus",
            new { iUserId = portfolioId, iDocumentID = id, strStatus = status }, commandType: CommandType.StoredProcedure), "Success");
    }

    public async Task<ProcResult> DeleteAsync(int portfolioId, int id)
    {
        await using var con = db.Create();
        return ProcResult.From(await con.ExecuteScalarAsync<string>("uspDeleteQuoteOrder",
            new { iUserId = portfolioId, iDocumentID = id }, commandType: CommandType.StoredProcedure), "Success");
    }

    /// <summary>Turns a quote into a draft invoice. The result's Id is the new invoice.</summary>
    public async Task<ProcResult> ToInvoiceAsync(int portfolioId, QuoteOrder quote, DateTime today, IReadOnlySet<int> accounts)
    {
        if (!quote.IsQuote || !quote.CanConvert)
            return ProcResult.Failed("This quote can't become an invoice.");
        var settings = await invoices.GetSettingsAsync(portfolioId);
        var saved = await invoices.SaveInvoiceAsync(portfolioId, null, quote.ToInvoice(today, settings.DueDays), false, accounts);
        return saved.Ok && saved.Id is { } invoiceId ? await MarkAsync(portfolioId, quote.Id, invoiceId) : saved;
    }

    /// <summary>Turns a purchase order into a draft bill. The result's Id is the new bill.</summary>
    public async Task<ProcResult> ToBillAsync(int portfolioId, QuoteOrder order, DateTime billDate, string? supplierInvoiceNumber, IReadOnlySet<int> accounts)
    {
        if (order.IsQuote || !order.CanConvert)
            return ProcResult.Failed("This purchase order can't become a bill.");
        var supplier = (await bills.GetSuppliersAsync(portfolioId)).FirstOrDefault(s => s.SupplierId == order.ContactId);
        var saved = await bills.SaveBillAsync(portfolioId, null, order.ToBill(billDate, supplier?.DueDays ?? 30, supplierInvoiceNumber), false, accounts);
        return saved.Ok && saved.Id is { } billId ? await MarkAsync(portfolioId, order.Id, billId) : saved;
    }

    private async Task<ProcResult> MarkAsync(int portfolioId, int id, int convertedId)
    {
        await using var con = db.Create();
        var result = ProcResult.From(await con.ExecuteScalarAsync<string>("uspMarkQuoteOrderConverted",
            new { iUserId = portfolioId, iDocumentID = id, iConvertedID = convertedId }, commandType: CommandType.StoredProcedure), "Success", convertedId);
        return result;
    }
}
