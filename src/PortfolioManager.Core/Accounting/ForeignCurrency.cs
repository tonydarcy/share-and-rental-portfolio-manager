using System.Data;
using System.Globalization;
using System.Xml.Linq;
using Dapper;
using PortfolioManager.Core.Data;

namespace PortfolioManager.Core.Accounting;

/// <summary>An account kept in another currency, with its balance in that currency and in the portfolio's.</summary>
public sealed record ForeignAccount(int AccountId, int Number, string Name, string Type, string Currency, decimal ForeignBalance, decimal HomeBalance)
{
    public string Display => $"{Number} - {Name} ({Currency})";
}

public sealed record ForeignEntry(int JournalId, DateOnly Date, string Description, string Source, decimal ForeignAmount, decimal HomeAmount);

/// <summary>An open invoice or bill (or a foreign account) as at the revaluation date.</summary>
public sealed record FxOpenItem(string Kind, int Id, string Document, string Currency, decimal ForeignAmount, decimal CarryingHome)
{
    /// <summary>Its value at the rate (foreign per one of the portfolio's currency), and the change from what is in the books.</summary>
    public decimal Revalued(decimal rate) => rate > 0 ? Math.Round(ForeignAmount / rate, 2, MidpointRounding.AwayFromZero) : CarryingHome;

    public decimal Change(decimal rate) => Revalued(rate) - CarryingHome;
}

public sealed record FxRevaluation(int Id, DateOnly Date, string? Rates, int? JournalId, int? ReversalJournalId, decimal Total);

public static class FxRevaluationRules
{
    public const string Invoice = "Invoice", Bill = "Bill", Account = "Account";

    /// <summary>
    /// The revaluation's journal lines. Invoices are debit balances (Accounts Receivable), bills credit balances (Accounts
    /// Payable), each foreign account its own balance. A rise in what is owed to us, or in the bank, is a gain; a rise in what
    /// we owe is a loss. Invoice and bill lines reverse the next day; account lines stay. Each set balances against the
    /// unrealised gains account.
    /// </summary>
    public static IReadOnlyList<(int AccountId, decimal Debit, decimal Credit, bool Reverse, string Description)> Lines(
        IEnumerable<FxOpenItem> items, IReadOnlyDictionary<string, decimal> rates, int receivableId, int payableId, int unrealisedId)
    {
        var lines = new List<(int, decimal, decimal, bool, string)>();
        decimal ar = 0, ap = 0;
        var accounts = new Dictionary<int, decimal>();
        foreach (var item in items)
        {
            if (!rates.TryGetValue(item.Currency, out var rate) || rate <= 0)
                continue;
            var change = item.Change(rate);
            switch (item.Kind)
            {
                case Invoice: ar += change; break;
                case Bill: ap += change; break;
                default: accounts[item.Id] = accounts.GetValueOrDefault(item.Id) + change; break;
            }
        }
        // Accounts Receivable up is a debit and a gain; Accounts Payable (a credit balance) up is a credit and a loss
        if (ar != 0)
            lines.Add((receivableId, Math.Max(ar, 0), Math.Max(-ar, 0), true, "Revaluation of foreign currency invoices"));
        if (ap != 0)
            lines.Add((payableId, Math.Max(-ap, 0), Math.Max(ap, 0), true, "Revaluation of foreign currency bills"));
        var reversing = ar - ap;
        if (reversing != 0)
            lines.Add((unrealisedId, Math.Max(-reversing, 0), Math.Max(reversing, 0), true, "Unrealised exchange gain or loss on invoices and bills"));
        foreach (var (accountId, change) in accounts.Where(a => a.Value != 0))
            lines.Add((accountId, Math.Max(change, 0), Math.Max(-change, 0), false, "Revaluation of foreign currency account"));
        var permanent = accounts.Values.Sum();
        if (permanent != 0)
            lines.Add((unrealisedId, Math.Max(-permanent, 0), Math.Max(permanent, 0), false, "Unrealised exchange gain or loss on foreign currency accounts"));
        return lines;
    }

    public static string LinesXml(IEnumerable<(int AccountId, decimal Debit, decimal Credit, bool Reverse, string Description)> lines) =>
        new XElement("lines", lines.Select(l => new XElement("l",
            new XAttribute("a", l.AccountId),
            new XAttribute("d", l.Debit.ToString(CultureInfo.InvariantCulture)),
            new XAttribute("c", l.Credit.ToString(CultureInfo.InvariantCulture)),
            new XAttribute("r", l.Reverse ? 1 : 0),
            new XAttribute("f", l.Description)))).ToString(SaveOptions.DisableFormatting);

    public static string RatesText(IReadOnlyDictionary<string, decimal> rates) =>
        string.Join(", ", rates.OrderBy(r => r.Key).Select(r => $"{r.Key} {r.Value.ToString("0.######", CultureInfo.InvariantCulture)}"));
}

/// <summary>Foreign currency accounts, money in and out of them, and revaluations (2026-10-21-23-foreign-currency.sql).</summary>
public sealed class ForeignCurrencyService(IDbConnectionFactory db)
{
    public async Task<IReadOnlyList<ForeignAccount>> GetAccountsAsync(int portfolioId, DateOnly? asAt = null)
    {
        await using var con = db.Create();
        try
        {
            var rows = await con.QueryAsync<(int Id, int Number, string Name, string Type, string Currency, decimal Foreign, decimal Home)>("""
                SELECT A.iAccountID, A.iAccountNumber, A.strAccountName, A.strAccountType, F.strCurrency,
                       ISNULL(SUM(L.dblForeignAmount), 0), ISNULL(SUM(ISNULL(L.dblDebit, 0) - ISNULL(L.dblCredit, 0)), 0)
                FROM tblForeignCurrencyAccounts F
                JOIN tblChartOfAccounts A ON A.iAccountID = F.iAccountID
                LEFT JOIN (tblJournalLines L JOIN tblJournal J ON J.iJournalID = L.iJournalID AND (@asAt IS NULL OR J.dtmTransactionDate <= @asAt))
                       ON L.iAccountID = F.iAccountID
                WHERE F.iUserID = @portfolioId
                GROUP BY A.iAccountID, A.iAccountNumber, A.strAccountName, A.strAccountType, F.strCurrency
                ORDER BY A.iAccountNumber
                """, new { portfolioId, asAt = asAt?.ToDateTime(TimeOnly.MinValue) });
            return rows.Select(r => new ForeignAccount(r.Id, r.Number, r.Name, r.Type, r.Currency.Trim(), r.Foreign, r.Home)).ToList();
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208)
        {
            return [];
        }
    }

    public async Task<ProcResult> SetCurrencyAsync(int portfolioId, int accountId, string? currency)
    {
        await using var con = db.Create();
        return ProcResult.From(await con.ExecuteScalarAsync<string>("uspSetAccountCurrency",
            new { iUserId = portfolioId, iAccountID = accountId, strCurrency = string.IsNullOrWhiteSpace(currency) ? null : currency.Trim().ToUpperInvariant() },
            commandType: CommandType.StoredProcedure), "Success");
    }

    public async Task<IReadOnlyList<ForeignEntry>> GetEntriesAsync(int portfolioId, int accountId)
    {
        await using var con = db.Create();
        var rows = await con.QueryAsync<(int Journal, DateTime Date, string? Description, string? Source, decimal? Foreign, decimal Home)>("""
            SELECT J.iJournalID, J.dtmTransactionDate, ISNULL(L.strDescription, J.strDescription), J.strSourceModule, L.dblForeignAmount,
                   ISNULL(L.dblDebit, 0) - ISNULL(L.dblCredit, 0)
            FROM tblJournalLines L JOIN tblJournal J ON J.iJournalID = L.iJournalID
            WHERE J.iUserID = @portfolioId AND L.iAccountID = @accountId
            ORDER BY J.dtmTransactionDate, J.iJournalID
            """, new { portfolioId, accountId });
        return rows.Select(r => new ForeignEntry(r.Journal, DateOnly.FromDateTime(r.Date), r.Description ?? "", r.Source ?? "", r.Foreign ?? 0, r.Home)).ToList();
    }

    /// <summary>Money in (positive) or out (negative) of a foreign currency account, against another account, at a rate.</summary>
    public async Task<ProcResult> AddEntryAsync(int portfolioId, int accountId, DateOnly date, decimal foreignAmount, decimal rate, int otherAccountId, string? description)
    {
        if (foreignAmount == 0 || rate <= 0)
            return ProcResult.Failed("Enter the amount and the exchange rate.");
        await using var con = db.Create();
        var row = await con.QuerySingleAsync<(string Result, int? Id)>("uspAddForeignBankEntry", new
        {
            iUserId = portfolioId, iAccountID = accountId, dtDate = date.ToDateTime(TimeOnly.MinValue), dblForeignAmount = foreignAmount,
            dblExchangeRate = rate, iOtherAccountID = otherAccountId,
            strDescription = string.IsNullOrWhiteSpace(description) ? (foreignAmount > 0 ? "Money in" : "Money out") : description.Trim(),
        }, commandType: CommandType.StoredProcedure);
        return ProcResult.From(row.Result, "Success", row.Id);
    }

    public async Task<ProcResult> DeleteEntryAsync(int portfolioId, int journalId)
    {
        await using var con = db.Create();
        return ProcResult.From(await con.ExecuteScalarAsync<string>("uspDeleteForeignBankEntry",
            new { iUserId = portfolioId, iJournalID = journalId }, commandType: CommandType.StoredProcedure), "Success");
    }

    /// <summary>
    /// Open foreign currency invoices and bills and the foreign accounts as at a date: what is left in their currency and
    /// what it is carried at in the books (invoices and bills at their own rate, accounts at their ledger balance).
    /// </summary>
    public async Task<IReadOnlyList<FxOpenItem>> GetOpenItemsAsync(int portfolioId, DateOnly asAt)
    {
        var date = asAt.ToDateTime(TimeOnly.MinValue);
        await using var con = db.Create();
        var invoices = await con.QueryAsync<(int Id, string Number, string Currency, decimal Total, decimal Home, string Kind, decimal Paid, decimal Cleared)>("""
            SELECT I.iInvoiceID, I.strInvoiceNumber + ' - ' + C.strName, I.strCurrency, I.dblTotal, ISNULL(I.dblHomeTotal, 0), I.strKind,
                   ISNULL((SELECT SUM(P.dblAmount) FROM tblInvoicePayments P WHERE P.iInvoiceID = I.iInvoiceID AND P.dtmPaymentDate <= @date), 0),
                   ISNULL((SELECT SUM(ISNULL(P.dblReceivableHome, P.dblAmount)) FROM tblInvoicePayments P WHERE P.iInvoiceID = I.iInvoiceID AND P.dtmPaymentDate <= @date), 0)
            FROM tblInvoices I JOIN tblCustomers C ON C.iCustomerID = I.iCustomerID
            WHERE I.iUserID = @portfolioId AND I.strCurrency IS NOT NULL AND I.strStatus = 'Approved' AND I.dtmIssueDate <= @date
            """, new { portfolioId, date });
        var bills = await con.QueryAsync<(int Id, string Number, string Currency, decimal Total, decimal Home, string Kind, decimal Paid, decimal Cleared)>("""
            SELECT B.iBillID, ISNULL(B.strBillNumber + ' - ', '') + S.strName, B.strCurrency, B.dblTotal, ISNULL(B.dblHomeTotal, 0), B.strKind,
                   ISNULL((SELECT SUM(P.dblAmount) FROM tblBillPayments P WHERE P.iBillID = B.iBillID AND P.dtmPaymentDate <= @date), 0),
                   ISNULL((SELECT SUM(ISNULL(P.dblPayableHome, P.dblAmount)) FROM tblBillPayments P WHERE P.iBillID = B.iBillID AND P.dtmPaymentDate <= @date), 0)
            FROM tblBills B JOIN tblSuppliers S ON S.iSupplierID = B.iSupplierID
            WHERE B.iUserID = @portfolioId AND B.strCurrency IS NOT NULL AND B.strStatus = 'Approved' AND B.dtmIssueDate <= @date
            """, new { portfolioId, date });
        var items = new List<FxOpenItem>();
        foreach (var i in invoices.Where(i => i.Total - i.Paid != 0))
        {
            var sign = i.Kind == "CreditNote" ? -1 : 1;
            items.Add(new FxOpenItem(FxRevaluationRules.Invoice, i.Id, i.Number, i.Currency.Trim(), sign * (i.Total - i.Paid), sign * (i.Home - i.Cleared)));
        }
        foreach (var b in bills.Where(b => b.Total - b.Paid != 0))
        {
            var sign = b.Kind == "SupplierCredit" ? -1 : 1;
            items.Add(new FxOpenItem(FxRevaluationRules.Bill, b.Id, b.Number, b.Currency.Trim(), sign * (b.Total - b.Paid), sign * (b.Home - b.Cleared)));
        }
        foreach (var a in await GetAccountsAsync(portfolioId, asAt))
            if (a.ForeignBalance != 0 || a.HomeBalance != 0)
                items.Add(new FxOpenItem(FxRevaluationRules.Account, a.AccountId, a.Display, a.Currency, a.ForeignBalance, a.HomeBalance));
        return items;
    }

    public async Task<IReadOnlyList<FxRevaluation>> GetRevaluationsAsync(int portfolioId)
    {
        await using var con = db.Create();
        try
        {
            var rows = await con.QueryAsync<(int Id, DateTime Date, string? Rates, int? Journal, int? Reversal, decimal Total)>("""
                SELECT R.iRevaluationID, R.dtmDate, R.strRates, R.iJournalID, R.iReversalJournalID,
                       ISNULL((SELECT SUM(L.dblDebit) FROM tblJournalLines L WHERE L.iJournalID = R.iJournalID), 0)
                FROM tblFxRevaluations R WHERE R.iUserID = @portfolioId ORDER BY R.dtmDate DESC
                """, new { portfolioId });
            return rows.Select(r => new FxRevaluation(r.Id, DateOnly.FromDateTime(r.Date), r.Rates, r.Journal, r.Reversal, r.Total)).ToList();
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208)
        {
            return [];
        }
    }

    /// <summary>Revalues every open foreign item at the rates on the date. Revaluations go forward: one on or after the date is refused.</summary>
    public async Task<ProcResult> RevalueAsync(int portfolioId, DateOnly date, IReadOnlyDictionary<string, decimal> rates)
    {
        if ((await GetRevaluationsAsync(portfolioId)).FirstOrDefault(r => r.Date >= date) is { } later)
            return ProcResult.Failed($"There is already a revaluation on {later.Date:d}. Delete it first, or revalue at a later date.");
        var items = await GetOpenItemsAsync(portfolioId, date);
        await using var con = db.Create();
        var ids = (await con.QueryAsync<(int Number, int Id)>("""
            SELECT iAccountNumber, iAccountID FROM tblChartOfAccounts WHERE iAccountNumber IN (1200, 2100, 4960) AND (iUserID = @portfolioId OR iUserID = 0)
            ORDER BY iUserID
            """, new { portfolioId })).GroupBy(a => a.Number).ToDictionary(g => g.Key, g => g.Last().Id);
        if (!ids.ContainsKey(1200) || !ids.ContainsKey(2100) || !ids.ContainsKey(4960))
            return ProcResult.Failed("Run the bills and foreign currency database migrations first.");
        var lines = FxRevaluationRules.Lines(items, rates, ids[1200], ids[2100], ids[4960]);
        var row = await con.QuerySingleAsync<(string Result, int? Id)>("uspPostFxRevaluation", new
        {
            iUserId = portfolioId, dtDate = date.ToDateTime(TimeOnly.MinValue), strRates = FxRevaluationRules.RatesText(rates),
            xmlLines = FxRevaluationRules.LinesXml(lines),
        }, commandType: CommandType.StoredProcedure);
        return ProcResult.From(row.Result, "Success", row.Id);
    }

    public async Task<ProcResult> DeleteRevaluationAsync(int portfolioId, int id)
    {
        await using var con = db.Create();
        return ProcResult.From(await con.ExecuteScalarAsync<string>("uspDeleteFxRevaluation",
            new { iUserId = portfolioId, iRevaluationID = id }, commandType: CommandType.StoredProcedure), "Success");
    }
}
