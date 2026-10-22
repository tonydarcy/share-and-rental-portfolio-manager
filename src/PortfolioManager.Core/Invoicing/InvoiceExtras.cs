using System.Data;
using System.Globalization;
using System.Xml.Linq;
using Dapper;
using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Data;

namespace PortfolioManager.Core.Invoicing;

public static class InvoiceKinds
{
    public const string Invoice = "Invoice", CreditNote = "CreditNote";
}

/// <summary>An invoice's kind (invoice or credit note) and, for a credit note, the invoice it credits.</summary>
public sealed record InvoiceKind(string Kind, int? CreditedInvoiceId)
{
    public static readonly InvoiceKind Plain = new(InvoiceKinds.Invoice, null);
    public bool IsCreditNote => Kind == InvoiceKinds.CreditNote;
}

/// <summary>An open invoice a deposit can pay: its balance is at least the deposit.</summary>
public sealed record DepositMatch(int InvoiceId, string Number, string Customer, DateOnly DueDate, decimal Balance, bool Exact);

public sealed record AgedBalance(int CustomerId, string Customer, decimal Current, decimal Days30, decimal Days60, decimal Days90, decimal Older)
{
    public decimal Total => Current + Days30 + Days60 + Days90 + Older;
}

public sealed record CustomerStatementLine(DateOnly Date, string Document, string Description, decimal Charged, decimal Paid, decimal Balance);

public sealed record CustomerStatement(Customer Customer, DateOnly From, DateOnly To, decimal Opening, IReadOnlyList<CustomerStatementLine> Lines,
    IReadOnlyList<AgedBalance> Aged)
{
    public decimal Closing => Lines.Count == 0 ? Opening : Lines[^1].Balance;
}

/// <summary>A movement on a customer's account, for the statement.</summary>
public sealed record AccountMovement(DateOnly Date, string Document, string Description, decimal Amount);

public static class Receivables
{
    /// <summary>
    /// What each customer owes at <paramref name="asAt"/>, by how long it is overdue: not yet due (current), 1 to 30
    /// days, 31 to 60, 61 to 90 and over 90. An unapplied credit note reduces the current column.
    /// </summary>
    public static IReadOnlyList<AgedBalance> Aged(IEnumerable<InvoiceSummary> invoices, IReadOnlyDictionary<int, InvoiceKind> kinds, DateOnly asAt) =>
        invoices.Where(i => i.Status == InvoiceStatus.Approved && i.IssueDate <= asAt && i.Balance != 0)
            .GroupBy(i => (i.CustomerId, i.CustomerName))
            .Select(g =>
            {
                decimal current = 0, d30 = 0, d60 = 0, d90 = 0, older = 0;
                foreach (var i in g)
                {
                    if (kinds.GetValueOrDefault(i.InvoiceId)?.IsCreditNote == true)
                    {
                        current -= i.Balance;
                        continue;
                    }
                    var days = asAt.DayNumber - i.DueDate.DayNumber;
                    switch (days)
                    {
                        case <= 0: current += i.Balance; break;
                        case <= 30: d30 += i.Balance; break;
                        case <= 60: d60 += i.Balance; break;
                        case <= 90: d90 += i.Balance; break;
                        default: older += i.Balance; break;
                    }
                }
                return new AgedBalance(g.Key.CustomerId, g.Key.CustomerName, current, d30, d60, d90, older);
            })
            .Where(a => a.Total != 0)
            .OrderBy(a => a.Customer, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    /// <summary>
    /// A customer's invoices, credit notes and payments as account movements: an invoice adds, a credit note and a
    /// payment take away. Applying a credit note to an invoice moves nothing (the credit note already took it away).
    /// </summary>
    public static IReadOnlyList<AccountMovement> Movements(IEnumerable<InvoiceDetail> documents, IReadOnlyDictionary<int, InvoiceKind> kinds)
    {
        var list = new List<AccountMovement>();
        foreach (var d in documents.Where(d => d.Status == InvoiceStatus.Approved))
        {
            var credit = kinds.GetValueOrDefault(d.InvoiceId)?.IsCreditNote == true;
            list.Add(new(d.IssueDate, (credit ? "Credit note " : "Invoice ") + d.Number, d.Reference ?? "", credit ? -d.Total : d.Total));
            if (credit)
                continue;
            foreach (var p in d.Payments.Where(p => !(p.Reference?.StartsWith("Credit note ", StringComparison.Ordinal) ?? false)))
                list.Add(new(p.Date, "Payment", $"For {d.Number}" + (p.Reference is { Length: > 0 } r ? $" ({r})" : ""), -p.Amount));
        }
        return list.OrderBy(m => m.Date).ThenByDescending(m => m.Amount).ToList();
    }

    public static (decimal Opening, IReadOnlyList<CustomerStatementLine> Lines) Statement(IReadOnlyList<AccountMovement> movements, DateOnly from, DateOnly to)
    {
        var opening = movements.Where(m => m.Date < from).Sum(m => m.Amount);
        var balance = opening;
        var lines = new List<CustomerStatementLine>();
        foreach (var m in movements.Where(m => m.Date >= from && m.Date <= to))
        {
            balance += m.Amount;
            lines.Add(new(m.Date, m.Document, m.Description, Math.Max(m.Amount, 0), Math.Max(-m.Amount, 0), balance));
        }
        return (opening, lines);
    }
}

public sealed record RepeatingInvoice(int Id, int CustomerId, string CustomerName, string Name, string Frequency, DateOnly Start, DateOnly? End,
    int PostedCount, int DueDays, bool Approve, bool AmountsIncludeTax, string? Reference, string? Notes, IReadOnlyList<InvoiceLineInput> Lines,
    bool Active, DateOnly? LastCreated)
{
    public DateOnly NextDate => RepeatFrequencies.Occurrence(Start, Frequency, PostedCount);

    public IReadOnlyList<DateOnly> DueDates(DateOnly asAt, int max = 36)
    {
        var dates = new List<DateOnly>();
        for (var n = PostedCount; Active && dates.Count < max; n++)
        {
            var d = RepeatFrequencies.Occurrence(Start, Frequency, n);
            if (d > asAt || (End is { } end && d > end))
                break;
            dates.Add(d);
        }
        return dates;
    }

    public InvoiceInput ToInput(DateOnly date) => new()
    {
        CustomerId = CustomerId,
        IssueDate = date.ToDateTime(TimeOnly.MinValue),
        DueDate = date.AddDays(DueDays).ToDateTime(TimeOnly.MinValue),
        Reference = Reference,
        Notes = Notes,
        AmountsIncludeTax = AmountsIncludeTax,
        Lines = Lines.Select(l => l.Copy()).ToList(),
    };

    public static string LinesXml(IEnumerable<InvoiceLineInput> lines) =>
        new XElement("lines", lines.Where(l => !l.IsBlank).Select(l => new XElement("l",
            new XAttribute("d", l.Description?.Trim() ?? ""),
            new XAttribute("q", (l.Quantity ?? 1).ToString(CultureInfo.InvariantCulture)),
            new XAttribute("p", (l.UnitPrice ?? 0).ToString(CultureInfo.InvariantCulture)),
            new XAttribute("a", l.AccountId),
            l.TaxCodeId is { } t ? new XAttribute("t", t) : null,
            l.ItemId is > 0 ? new XAttribute("i", l.ItemId) : null,
            l.Tracking1Id is > 0 ? new XAttribute("t1", l.Tracking1Id) : null,
            l.Tracking2Id is > 0 ? new XAttribute("t2", l.Tracking2Id) : null))).ToString(SaveOptions.DisableFormatting);

    public static IReadOnlyList<InvoiceLineInput> ParseLines(string? xml) =>
        string.IsNullOrWhiteSpace(xml) ? [] : XElement.Parse(xml).Elements("l").Select(e => new InvoiceLineInput
        {
            Description = e.Attribute("d")?.Value,
            Quantity = decimal.TryParse(e.Attribute("q")?.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var q) ? q : 1,
            UnitPrice = decimal.TryParse(e.Attribute("p")?.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var p) ? p : 0,
            AccountId = (int?)e.Attribute("a") ?? 0,
            TaxCodeId = (int?)e.Attribute("t"),
            ItemId = (int?)e.Attribute("i"),
            Tracking1Id = (int?)e.Attribute("t1"),
            Tracking2Id = (int?)e.Attribute("t2"),
        }).ToList();
}

public sealed class RepeatingInvoiceInput
{
    public string? Name { get; set; }
    public string Frequency { get; set; } = RepeatFrequencies.Monthly;
    public DateTime? End { get; set; }
    public bool Approve { get; set; }
}

/// <summary>
/// Credit notes, matching imported deposits to invoices, repeating invoices and the logo
/// (2026-10-21-17-invoicing-extras.sql). Invoices themselves are saved by <see cref="InvoiceService"/>.
/// </summary>
public sealed class InvoiceExtrasService(IDbConnectionFactory db, InvoiceService invoices)
{
    /// <summary>The credit notes, by id; every other invoice is a plain invoice. Empty until the migration has been run.</summary>
    public async Task<IReadOnlyDictionary<int, InvoiceKind>> GetKindsAsync(int portfolioId)
    {
        await using var con = db.Create();
        try
        {
            var rows = await con.QueryAsync<(int Id, string Kind, int? Credited)>(
                "SELECT iInvoiceID, strKind, iCreditedInvoiceID FROM tblInvoices WHERE iUserID = @portfolioId AND strKind <> 'Invoice'", new { portfolioId });
            return rows.ToDictionary(r => r.Id, r => new InvoiceKind(r.Kind, r.Credited));
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 207)
        {
            return new Dictionary<int, InvoiceKind>();
        }
    }

    /// <summary>A draft credit note with the invoice's customer and lines, for the user to change and approve.</summary>
    public async Task<ProcResult> CreateCreditNoteAsync(int portfolioId, int invoiceId, IReadOnlySet<int> incomeAccounts)
    {
        if (await invoices.GetInvoiceAsync(portfolioId, invoiceId) is not { IsDraft: false, IsVoid: false } invoice)
            return ProcResult.Failed("Only an approved invoice can be credited.");
        var input = InvoiceInput.From(invoice);
        input.IssueDate = input.DueDate = DateTime.Today;
        input.Reference = $"Credit for invoice {invoice.Number}";
        var saved = await invoices.SaveInvoiceAsync(portfolioId, null, input, approve: false, incomeAccounts);
        if (!saved.Ok)
            return saved;
        await using var con = db.Create();
        var kind = ProcResult.From(await con.ExecuteScalarAsync<string>("uspSetInvoiceKind",
            new { iUserId = portfolioId, iInvoiceID = saved.Id, strKind = InvoiceKinds.CreditNote, iCreditedInvoiceID = invoiceId },
            commandType: CommandType.StoredProcedure), "Success");
        return kind.Ok ? saved : kind;
    }

    public async Task<ProcResult> ApplyCreditNoteAsync(int portfolioId, int creditNoteId, int invoiceId, DateOnly date, decimal amount)
    {
        await using var con = db.Create();
        return ProcResult.From(await con.ExecuteScalarAsync<string>("uspApplyCreditNote",
            new { iUserId = portfolioId, iCreditNoteID = creditNoteId, iInvoiceID = invoiceId, dtDate = date.ToDateTime(TimeOnly.MinValue), dblAmount = amount },
            commandType: CommandType.StoredProcedure), "Success");
    }

    /// <summary>Approved invoices owing at least <paramref name="amount"/>, the ones owing exactly that first.</summary>
    public async Task<IReadOnlyList<DepositMatch>> GetDepositMatchesAsync(int portfolioId, decimal amount)
    {
        if (amount <= 0)
            return [];
        var kinds = await GetKindsAsync(portfolioId);
        return (await invoices.GetInvoicesAsync(portfolioId))
            .Where(i => i.Balance >= amount && !kinds.ContainsKey(i.InvoiceId))
            .Select(i => new DepositMatch(i.InvoiceId, i.Number, i.CustomerName, i.DueDate, i.Balance, i.Balance == amount))
            .OrderByDescending(m => m.Exact).ThenBy(m => m.DueDate)
            .ToList();
    }

    public async Task<ProcResult> MatchDepositAsync(int portfolioId, int lineId, int invoiceId)
    {
        await using var con = db.Create();
        var row = await con.QuerySingleAsync<(string Result, int? Id)>("uspMatchInvoiceDeposit",
            new { iUserId = portfolioId, iInvoiceID = invoiceId, iSuspenseLineID = lineId }, commandType: CommandType.StoredProcedure);
        return ProcResult.From(row.Result, "Success", row.Id);
    }

    public async Task<(byte[] Data, string Type)?> GetLogoAsync(int portfolioId)
    {
        await using var con = db.Create();
        try
        {
            var row = await con.QuerySingleOrDefaultAsync<(byte[]? Data, string? Type)>(
                "SELECT binLogo, strLogoType FROM tblInvoiceSettings WHERE iUserID = @portfolioId", new { portfolioId });
            return row is { Data: { Length: > 0 } d, Type: { } t } ? (d, t) : null;
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 207)
        {
            return null;
        }
    }

    public static readonly IReadOnlyDictionary<string, string> LogoTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".gif"] = "image/gif", [".webp"] = "image/webp",
    };

    /// <summary>Saves the logo (by its file name's type), or removes it when <paramref name="data"/> is null.</summary>
    public async Task<ProcResult> SaveLogoAsync(int portfolioId, string? fileName, byte[]? data)
    {
        string? type = null;
        if (data is not null && (!LogoTypes.TryGetValue(Path.GetExtension(fileName ?? ""), out type) || data.Length > 1_048_576))
            return ProcResult.Failed("Use a PNG, JPEG, GIF or WebP picture of up to 1 MB.");
        await using var con = db.Create();
        return ProcResult.From(await con.ExecuteScalarAsync<string>("uspSaveInvoiceLogo",
            new { iUserId = portfolioId, binLogo = data, strLogoType = type }, commandType: CommandType.StoredProcedure), "Success");
    }

    private sealed record RepeatingRow(int Id, int CustomerId, string CustomerName, string Name, string Frequency, DateTime Start, DateTime? End,
        int PostedCount, int DueDays, bool Approve, bool AmountsIncludeTax, string? Reference, string? Notes, string LinesXml, bool Active, DateTime? LastCreated);

    public async Task<IReadOnlyList<RepeatingInvoice>> GetRepeatingAsync(int portfolioId)
    {
        await using var con = db.Create();
        try
        {
            var rows = await con.QueryAsync<RepeatingRow>("""
                SELECT R.iRepeatingInvoiceID AS Id, R.iCustomerID AS CustomerId, C.strName AS CustomerName, R.strName AS Name, R.strFrequency AS Frequency,
                       CAST(R.dtmStart AS datetime) AS Start, CAST(R.dtmEnd AS datetime) AS [End], R.iPostedCount AS PostedCount, R.iDueDays AS DueDays,
                       R.bApprove AS Approve, R.bAmountsIncludeTax AS AmountsIncludeTax, R.strReference AS Reference, R.strNotes AS Notes,
                       R.strLinesXml AS LinesXml, R.bActive AS Active, CAST(R.dtmLastCreated AS datetime) AS LastCreated
                FROM tblRepeatingInvoices R JOIN tblCustomers C ON C.iCustomerID = R.iCustomerID
                WHERE R.iUserID = @portfolioId ORDER BY R.strName
                """, new { portfolioId });
            return rows.Select(r => new RepeatingInvoice(r.Id, r.CustomerId, r.CustomerName, r.Name, r.Frequency, DateOnly.FromDateTime(r.Start),
                r.End is { } e ? DateOnly.FromDateTime(e) : null, r.PostedCount, r.DueDays, r.Approve, r.AmountsIncludeTax, r.Reference, r.Notes,
                RepeatingInvoice.ParseLines(r.LinesXml), r.Active, r.LastCreated is { } l ? DateOnly.FromDateTime(l) : null)).ToList();
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208)
        {
            return [];
        }
    }

    /// <summary>Keeps an invoice as a repeating one, from its issue date. Nothing is created until it is due.</summary>
    public async Task<ProcResult> SaveRepeatingAsync(int portfolioId, int id, InvoiceInput input, RepeatingInvoiceInput repeat)
    {
        if (!RepeatFrequencies.All.Contains(repeat.Frequency))
            return ProcResult.Failed("Choose how often it repeats.");
        await using var con = db.Create();
        var row = await con.QuerySingleAsync<(string Result, int? Id)>("uspSaveRepeatingInvoice", new
        {
            iUserId = portfolioId, iRepeatingInvoiceID = id, iCustomerID = input.CustomerId,
            strName = string.IsNullOrWhiteSpace(repeat.Name) ? $"Every {RepeatFrequencies.Label(repeat.Frequency).ToLowerInvariant()}" : repeat.Name.Trim(),
            strFrequency = repeat.Frequency, dtmStart = input.IssueDate.Date, dtmEnd = repeat.End?.Date,
            iDueDays = Math.Clamp((input.DueDate.Date - input.IssueDate.Date).Days, 0, 365), bApprove = repeat.Approve,
            bAmountsIncludeTax = input.AmountsIncludeTax, strReference = input.Reference, strNotes = input.Notes,
            strLinesXml = RepeatingInvoice.LinesXml(input.Lines),
        }, commandType: CommandType.StoredProcedure);
        return ProcResult.From(row.Result, "Success", row.Id);
    }

    public async Task<ProcResult> SetRepeatingActiveAsync(int portfolioId, int id, bool active) =>
        await ResultAsync("uspSetRepeatingInvoiceActive", new { iUserId = portfolioId, iRepeatingInvoiceID = id, bActive = active });

    public async Task<ProcResult> DeleteRepeatingAsync(int portfolioId, int id) =>
        await ResultAsync("uspDeleteRepeatingInvoice", new { iUserId = portfolioId, iRepeatingInvoiceID = id });

    /// <summary>
    /// Creates every repeating invoice due up to <paramref name="asAt"/>. The count moves on before each one is made
    /// (so two people pressing at once can't make it twice) and back if it then can't be saved.
    /// </summary>
    public async Task<(int Created, IReadOnlyList<string> Errors)> CreateDueAsync(int portfolioId, DateOnly asAt, IReadOnlySet<int> incomeAccounts)
    {
        var created = 0;
        var errors = new List<string>();
        foreach (var r in await GetRepeatingAsync(portfolioId))
        {
            var count = r.PostedCount;
            foreach (var date in r.DueDates(asAt))
            {
                await using (var con = db.Create())
                {
                    var mark = ProcResult.From(await con.ExecuteScalarAsync<string>("uspMarkRepeatingInvoice",
                        new { iUserId = portfolioId, iRepeatingInvoiceID = r.Id, iPostedCount = count, iBy = 1, dtmCreated = date.ToDateTime(TimeOnly.MinValue) },
                        commandType: CommandType.StoredProcedure), "Success");
                    if (!mark.Ok)
                    {
                        errors.Add($"{r.Name}: {mark.Error}");
                        break;
                    }
                }
                ProcResult saved;
                try
                {
                    saved = await invoices.SaveInvoiceAsync(portfolioId, null, r.ToInput(date), r.Approve, incomeAccounts);
                }
                catch (Exception ex)
                {
                    saved = ProcResult.Failed(LockedPeriodService.IsLockError(ex) ? "that date is in a locked period." : ex.Message);
                }
                if (!saved.Ok)
                {
                    await using var con = db.Create();
                    await con.ExecuteScalarAsync<string>("uspMarkRepeatingInvoice",
                        new { iUserId = portfolioId, iRepeatingInvoiceID = r.Id, iPostedCount = count + 1, iBy = -1, dtmCreated = (DateTime?)null },
                        commandType: CommandType.StoredProcedure);
                    errors.Add($"{r.Name} on {date:d}: {saved.Error}");
                    break;
                }
                count++;
                created++;
            }
        }
        return (created, errors);
    }

    private async Task<ProcResult> ResultAsync(string proc, object args)
    {
        await using var con = db.Create();
        return ProcResult.From(await con.ExecuteScalarAsync<string>(proc, args, commandType: CommandType.StoredProcedure), "Success");
    }
}
