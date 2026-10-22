using System.Data;
using System.Xml.Linq;
using Dapper;
using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Data;
using PortfolioManager.Core.SalesTax;

namespace PortfolioManager.Core.Claims;

public static class ClaimStatus
{
    public const string Submitted = "Submitted", Approved = "Approved", Rejected = "Rejected", Paid = "Paid";
    public static readonly IReadOnlyList<string> All = [Submitted, Approved, Rejected, Paid];
}

/// <summary>Something a person paid for out of their own pocket and wants back. Approving posts it; paying clears it.</summary>
public sealed record ExpenseClaim(
    int Id,
    int? ClaimantUserId,
    string Claimant,
    DateOnly Date,
    string? Payee,
    string Description,
    int AccountId,
    string AccountName,
    int? TaxCodeId,
    decimal Amount,
    decimal Tax,
    int? Tracking1Id,
    int? Tracking2Id,
    string Status,
    string? RejectReason,
    DateTime? Approved,
    int? JournalId,
    int? PaymentJournalId,
    DateOnly? Paid,
    DateTime Submitted)
{
    public decimal Net => Amount - Tax;
}

/// <summary>The claim form. Amount includes the tax; the tax is worked out from the tax code unless it is typed in.</summary>
public sealed class ClaimInput
{
    public string? Claimant { get; set; }
    public DateTime Date { get; set; }
    public string? Payee { get; set; }
    public string? Description { get; set; }
    public int AccountId { get; set; }
    public int? TaxCodeId { get; set; }
    public decimal Amount { get; set; }
    public decimal? Tax { get; set; }
    public int? Tracking1Id { get; set; }
    public int? Tracking2Id { get; set; }

    public static ClaimInput From(ExpenseClaim c) => new()
    {
        Claimant = c.Claimant, Date = c.Date.ToDateTime(TimeOnly.MinValue), Payee = c.Payee, Description = c.Description, AccountId = c.AccountId,
        TaxCodeId = c.TaxCodeId, Amount = c.Amount, Tax = c.Tax, Tracking1Id = c.Tracking1Id, Tracking2Id = c.Tracking2Id,
    };

    public decimal TaxFor(IReadOnlyDictionary<int, TaxCode> taxCodes) =>
        TaxCodeId is { } id && taxCodes.TryGetValue(id, out var code) ? Tax ?? TaxMath.TaxInGross(Amount, code.Rate) : 0;

    public IReadOnlyList<string> Validate(IReadOnlySet<int> accounts, IReadOnlyDictionary<int, TaxCode> taxCodes)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Claimant))
            errors.Add("Enter who is to be paid back.");
        if (Date == default)
            errors.Add("Enter the receipt's date.");
        if (string.IsNullOrWhiteSpace(Description))
            errors.Add("Say what it was for.");
        else if (Description.Trim().Length > 500)
            errors.Add("The description can be at most 500 characters.");
        if (!accounts.Contains(AccountId))
            errors.Add("Choose the expense account.");
        if (TaxCodeId is { } t && !taxCodes.ContainsKey(t))
            errors.Add("Choose a tax code from the list.");
        if (Amount <= 0)
            errors.Add("Enter the amount paid, including any tax.");
        else if (TaxFor(taxCodes) is var tax && (tax < 0 || tax > Amount))
            errors.Add("The tax can't be more than the amount.");
        return errors;
    }
}

/// <summary>The claims waiting to be paid back to one person.</summary>
public sealed record ClaimantOwing(string Claimant, int Count, decimal Amount);

public static class ClaimRules
{
    public static IReadOnlyList<ClaimantOwing> Owing(IEnumerable<ExpenseClaim> claims) =>
        claims.Where(c => c.Status == ClaimStatus.Approved)
            .GroupBy(c => c.Claimant.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => new ClaimantOwing(g.First().Claimant, g.Count(), g.Sum(c => c.Amount)))
            .OrderBy(o => o.Claimant)
            .ToList();

    public static string ClaimsXml(IEnumerable<int> ids) =>
        new XElement("claims", ids.Distinct().Select(id => new XElement("c", new XAttribute("id", id)))).ToString(SaveOptions.DisableFormatting);
}

/// <summary>Expense claims: submit, approve (posts Dr expense, Cr 2150 Expense Claims Payable), pay back (2026-10-21-28-expense-claims.sql).</summary>
public sealed class ExpenseClaimService(IDbConnectionFactory db, SalesTaxService salesTax)
{
    public async Task<IReadOnlyList<ExpenseClaim>> GetAsync(int portfolioId, int? id = null)
    {
        await using var con = db.Create();
        try
        {
            var rows = await con.QueryAsync("""
                SELECT C.*, A.strAccountName FROM tblExpenseClaims C
                JOIN tblChartOfAccounts A ON A.iAccountID = C.iAccountID
                WHERE C.iUserID = @portfolioId AND (@id IS NULL OR C.iClaimID = @id)
                ORDER BY CASE C.strStatus WHEN 'Submitted' THEN 0 WHEN 'Approved' THEN 1 ELSE 2 END, C.dtmDate DESC, C.iClaimID DESC
                """, new { portfolioId, id });
            return rows.Cast<IDictionary<string, object?>>().Select(Map).ToList();
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208)
        {
            return [];
        }
    }

    public async Task<ExpenseClaim?> GetOneAsync(int portfolioId, int id) => (await GetAsync(portfolioId, id)).FirstOrDefault();

    public static ExpenseClaim Map(IDictionary<string, object?> r)
    {
        string? Optional(string column) => Db.Value(r, column) is null ? null : Db.Str(r, column);
        return new ExpenseClaim(
            Db.Int(r, "iClaimID") ?? 0,
            Db.Int(r, "iClaimantUserID"),
            Db.Str(r, "strClaimant"),
            Db.Date(r, "dtmDate") ?? default,
            Optional("strPayee"),
            Db.Str(r, "strDescription"),
            Db.Int(r, "iAccountID") ?? 0,
            Db.Str(r, "strAccountName"),
            Db.Int(r, "iTaxCodeID"),
            Db.Dec(r, "dblAmount") ?? 0,
            Db.Dec(r, "dblTax") ?? 0,
            Db.Int(r, "iTrackingOption1ID"),
            Db.Int(r, "iTrackingOption2ID"),
            Db.Str(r, "strStatus"),
            Optional("strRejectReason"),
            Db.Timestamp(r, "dtmApproved"),
            Db.Int(r, "iJournalID"),
            Db.Int(r, "iPaymentJournalID"),
            Db.Date(r, "dtmPaid"),
            Db.Timestamp(r, "dtmSubmitted") ?? default);
    }

    /// <summary>Saves a new claim, or changes one that is still waiting for approval.</summary>
    public async Task<ProcResult> SaveAsync(int portfolioId, int? id, int loginUserId, ClaimInput input, IReadOnlySet<int> accounts)
    {
        var taxCodes = (await salesTax.GetCodesAsync(portfolioId)).ToDictionary(c => c.TaxCodeId);
        if (input.Validate(accounts, taxCodes) is [var first, ..])
            return ProcResult.Failed(first);
        await using var con = db.Create();
        var row = await con.QuerySingleAsync<(string Result, int? Id)>("uspSaveExpenseClaim", SaveParameters(portfolioId, id, loginUserId, input, taxCodes),
            commandType: CommandType.StoredProcedure);
        return ProcResult.From(row.Result, "Success", row.Id);
    }

    public static object SaveParameters(int portfolioId, int? id, int loginUserId, ClaimInput input, IReadOnlyDictionary<int, TaxCode> taxCodes) => new
    {
        iUserId = portfolioId,
        iClaimID = id,
        iClaimantUserID = loginUserId,
        strClaimant = input.Claimant?.Trim(),
        dtDate = input.Date.Date,
        strPayee = string.IsNullOrWhiteSpace(input.Payee) ? null : input.Payee.Trim(),
        strDescription = input.Description?.Trim(),
        iAccountID = input.AccountId,
        iTaxCodeID = input.TaxCodeId,
        dblAmount = input.Amount,
        dblTax = input.TaxFor(taxCodes),
        iTracking1ID = input.Tracking1Id is > 0 ? input.Tracking1Id : null,
        iTracking2ID = input.Tracking2Id is > 0 ? input.Tracking2Id : null,
    };

    public async Task<ProcResult> ApproveAsync(int portfolioId, int id, int approverUserId) =>
        await RunAsync("uspApproveExpenseClaim", new { iUserId = portfolioId, iClaimID = id, iApproverUserID = approverUserId });

    public async Task<ProcResult> RejectAsync(int portfolioId, int id, string? reason) =>
        await RunAsync("uspRejectExpenseClaim", new { iUserId = portfolioId, iClaimID = id, strReason = reason?.Trim() });

    public async Task<ProcResult> UnapproveAsync(int portfolioId, int id) =>
        await RunAsync("uspUnapproveExpenseClaim", new { iUserId = portfolioId, iClaimID = id });

    public async Task<ProcResult> DeleteAsync(int portfolioId, int id) =>
        await RunAsync("uspDeleteExpenseClaim", new { iUserId = portfolioId, iClaimID = id });

    /// <summary>Pays back approved claims from a bank account in one payment. The result's Id is the payment journal.</summary>
    public async Task<ProcResult> PayAsync(int portfolioId, IReadOnlyCollection<int> claimIds, DateOnly date, int bankAccountId)
    {
        if (claimIds.Count == 0)
            return ProcResult.Failed("Tick the claims to pay.");
        await using var con = db.Create();
        var row = await con.QuerySingleAsync<(string Result, int? Id)>("uspPayExpenseClaims", new
        {
            iUserId = portfolioId, xmlClaims = ClaimRules.ClaimsXml(claimIds), dtDate = date.ToDateTime(TimeOnly.MinValue), iAccountID = bankAccountId,
        }, commandType: CommandType.StoredProcedure);
        return ProcResult.From(row.Result, "Success", row.Id);
    }

    public async Task<ProcResult> UndoPaymentAsync(int portfolioId, int journalId) =>
        await RunAsync("uspUndoExpenseClaimPayment", new { iUserId = portfolioId, iJournalID = journalId });

    private async Task<ProcResult> RunAsync(string proc, object parameters)
    {
        await using var con = db.Create();
        return ProcResult.From(await con.ExecuteScalarAsync<string>(proc, parameters, commandType: CommandType.StoredProcedure), "Success");
    }
}
