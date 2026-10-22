using System.Data;
using System.Data.Common;
using Dapper;
using PortfolioManager.Core.Data;
using PortfolioManager.Core.SalesTax;

namespace PortfolioManager.Core.Accounting;

public sealed record JournalSummary(int JournalId, DateOnly Date, string Description, string SourceModule, decimal TotalAmount)
{
    public bool IsManual => JournalRules.IsManual(SourceModule);
}

/// <summary>
/// A journal line. A line with a tax code holds the amount before tax and its TaxAmount; the tax
/// itself is a second line to the tax control account (IsTaxLine).
/// </summary>
public sealed record JournalLine(
    int LineId,
    int AccountId,
    string AccountName,
    decimal Debit,
    decimal Credit,
    string Description,
    int? MatchedJournalId,
    int? TaxCodeId = null,
    decimal? TaxAmount = null,
    bool IsTaxLine = false,
    int? Tracking1Id = null,
    int? Tracking2Id = null);

public sealed record JournalDetail(
    int JournalId,
    DateOnly Date,
    string Description,
    string SourceModule,
    int? SourceId,
    string? BatchId,
    DateTime? Created,
    IReadOnlyList<JournalLine> Lines)
{
    public bool IsManual => JournalRules.IsManual(SourceModule);
    public decimal TotalDebit => Lines.Sum(l => l.Debit);
    public decimal TotalCredit => Lines.Sum(l => l.Credit);
}

public enum JournalChangeOutcome { Saved, NotFound, NotManual, Invalid }

public sealed record JournalChangeResult(JournalChangeOutcome Outcome, int? JournalId = null, IReadOnlyList<string>? Errors = null);

/// <summary>Manual journals and the journal history (JournalEntry.aspx, JournalHistory.aspx).</summary>
public sealed class JournalService(IDbConnectionFactory db, ChartOfAccountsService accounts, RealisedGainService gains, SalesTaxService salesTax)
{
    public async Task<IReadOnlyList<JournalSummary>> ListAsync(int portfolioId, DateOnly? from, DateOnly? to)
    {
        await gains.SyncAsync(portfolioId);
        await using var con = db.Create();
        var rows = await con.QueryAsync("uspGetJournalList", new
        {
            iUserId = portfolioId,
            dtStartDate = from?.ToDateTime(TimeOnly.MinValue),
            dtEndDate = to?.ToDateTime(TimeOnly.MinValue),
        }, commandType: CommandType.StoredProcedure);
        return MapList(rows.Cast<IDictionary<string, object?>>());
    }

    public static IReadOnlyList<JournalSummary> MapList(IEnumerable<IDictionary<string, object?>> rows) =>
        rows.Select(r => new JournalSummary(
                Db.Int(r, "iJournalID") ?? 0,
                Db.Date(r, "dtmTransactionDate") ?? default,
                Db.Str(r, "strDescription"),
                Db.Str(r, "strSourceModule"),
                Db.Dec(r, "TotalAmount") ?? 0))
            .Where(j => j.JournalId > 0)
            .ToList();

    /// <summary>The journal and its lines, only if it belongs to the portfolio; otherwise null.</summary>
    public async Task<JournalDetail?> GetAsync(int portfolioId, int journalId)
    {
        await gains.SyncAsync(portfolioId);
        await using var con = db.Create();
        return await GetAsync(con, null, portfolioId, journalId);
    }

    private static async Task<JournalDetail?> GetAsync(DbConnection con, DbTransaction? tx, int portfolioId, int journalId)
    {
        // Two result sets: the header (filtered by portfolio), then the lines
        await using var grid = await con.QueryMultipleAsync("uspGetJournalDetail",
            new { iUserId = portfolioId, iJournalID = journalId }, tx, commandType: CommandType.StoredProcedure);
        var header = (await grid.ReadAsync()).Cast<IDictionary<string, object?>>().FirstOrDefault();
        var lines = grid.IsConsumed ? [] : (await grid.ReadAsync()).Cast<IDictionary<string, object?>>().ToList();
        return MapDetail(header, lines, portfolioId);
    }

    /// <summary>
    /// The proc only filters the header by portfolio; its lines query doesn't, so nothing is
    /// returned unless the header came back and belongs to the portfolio.
    /// </summary>
    public static JournalDetail? MapDetail(IDictionary<string, object?>? header, IEnumerable<IDictionary<string, object?>> lines, int portfolioId)
    {
        if (header is null || Db.Int(header, "iUserID") != portfolioId || Db.Int(header, "iJournalID") is not { } journalId)
            return null;

        return new JournalDetail(
            journalId,
            Db.Date(header, "dtmTransactionDate") ?? default,
            Db.Str(header, "strDescription"),
            Db.Str(header, "strSourceModule"),
            Db.Int(header, "iSourceID"),
            Db.Str(header, "strBatchID") is { Length: > 0 } b ? b : null,
            Db.Timestamp(header, "dtmCreatedDate"),
            lines.Where(l => Db.Int(l, "iJournalID") == journalId)
                .Select(l => new JournalLine(
                    Db.Int(l, "iJournalLineID") ?? 0,
                    Db.Int(l, "iAccountID") ?? 0,
                    Db.Str(l, "strAccountName"),
                    Db.Dec(l, "dblDebit") ?? 0,
                    Db.Dec(l, "dblCredit") ?? 0,
                    Db.Str(l, "strDescription"),
                    Db.Int(l, "iMatchedJournalID"),
                    Db.Int(l, "iTaxCodeID"),
                    Db.Dec(l, "dblTaxAmount"),
                    Db.Bool(l, "bIsTaxLine") ?? false,
                    Db.Int(l, "iTrackingOption1ID"),
                    Db.Int(l, "iTrackingOption2ID")))
                .OrderByDescending(l => l.Debit).ThenBy(l => l.LineId)
                .ToList());
    }

    /// <summary>
    /// The form for editing a manual journal: its lines plus one empty row. A line with a tax code
    /// goes back to its amount including tax, and the tax line split out of it is left off (saving
    /// splits it out again). The tax is only filled in when it isn't the code's rate.
    /// </summary>
    /// <summary>Tags the lines just added for a form line (its tax line too) with the line's tracking options.</summary>
    private static Task TagAsync(DbConnection con, DbTransaction tx, int portfolioId, int journalId, int afterLineId, JournalLineInput line) =>
        con.ExecuteAsync("uspSetJournalLineTracking", new
        {
            iUserId = portfolioId, iJournalID = journalId, iAfterLineID = afterLineId, iOption1ID = line.Tracking1Id, iOption2ID = line.Tracking2Id,
        }, tx, commandType: CommandType.StoredProcedure);

    public static JournalInput ToInput(JournalDetail j, IReadOnlyDictionary<int, TaxCode>? taxCodes = null) => new()
    {
        Date = j.Date.ToDateTime(TimeOnly.MinValue),
        Description = j.Description,
        Lines = [.. j.Lines.Where(l => !l.IsTaxLine).Select(l =>
        {
            var tax = l.TaxCodeId is null ? 0 : l.TaxAmount ?? 0;
            var debit = l.Debit == 0 ? (decimal?)null : l.Debit + tax;
            var credit = l.Credit == 0 ? (decimal?)null : l.Credit + tax;
            var gross = (debit ?? 0) + (credit ?? 0);
            var usual = l.TaxCodeId is { } id && taxCodes?.TryGetValue(id, out var code) == true
                ? TaxMath.TaxInGross(gross, code.Rate)
                : (decimal?)null;
            return new JournalLineInput
            {
                AccountId = l.AccountId,
                Description = l.Description == j.Description ? null : l.Description,
                Debit = debit,
                Credit = credit,
                TaxCodeId = l.TaxCodeId,
                Tax = l.TaxCodeId is null || usual == tax ? null : tax,
                Tracking1Id = l.Tracking1Id,
                Tracking2Id = l.Tracking2Id,
            };
        }), new JournalLineInput()],
    };

    /// <summary>
    /// Saves a new manual journal (uspAddJournalHeader) or replaces the lines of an existing one
    /// (uspUpdateJournalHeader + uspClearJournalLines), then adds the lines with uspAddJournalLine,
    /// all in one transaction as the legacy page did. Only a manual journal of this portfolio can
    /// be edited, and the saved lines are checked to balance before it commits.
    /// <paramref name="alsoInTransaction"/> runs in the same transaction with the journal's id, before the commit.
    /// </summary>
    public async Task<JournalChangeResult> SaveAsync(int portfolioId, int? journalId, JournalInput input,
        Func<DbConnection, DbTransaction, int, Task>? alsoInTransaction = null)
    {
        var allowed = (await accounts.GetJournalAccountsAsync(portfolioId)).Select(a => a.AccountId).ToHashSet();
        var taxCodes = (await salesTax.GetActiveCodesAsync(portfolioId)).ToDictionary(c => c.TaxCodeId);
        var errors = input.Validate(allowed, taxCodes);
        if (errors.Count > 0)
            return new JournalChangeResult(JournalChangeOutcome.Invalid, journalId, errors);

        var description = input.Description.Trim();
        await using var con = db.Create();
        await con.OpenAsync();
        await using var tx = await con.BeginTransactionAsync();

        int id;
        if (journalId is { } existingId)
        {
            var existing = await GetAsync(con, tx, portfolioId, existingId);
            if (existing is null)
                return new JournalChangeResult(JournalChangeOutcome.NotFound);
            if (!existing.IsManual)
                return new JournalChangeResult(JournalChangeOutcome.NotManual, existingId);

            await con.ExecuteAsync("uspUpdateJournalHeader", new
            {
                iUserId = portfolioId,
                iJournalID = existingId,
                dtDate = input.Date.Date,
                strDescription = description,
            }, tx, commandType: CommandType.StoredProcedure);
            await con.ExecuteAsync("uspClearJournalLines", new { iJournalID = existingId },
                tx, commandType: CommandType.StoredProcedure);
            id = existingId;
        }
        else
        {
            // SCOPE_IDENTITY() comes back as a decimal
            var newId = await con.ExecuteScalarAsync<decimal?>("uspAddJournalHeader", new
            {
                iUserId = portfolioId,
                dtDate = input.Date.Date,
                strDescription = description,
            }, tx, commandType: CommandType.StoredProcedure);
            if (newId is null or <= 0)
                throw new InvalidOperationException("uspAddJournalHeader did not return the new journal id.");
            id = (int)newId.Value;
        }

        var expectedLines = 0;
        foreach (var line in input.UsedLines)
        {
            // As on the legacy page, a line without its own description takes the journal's
            var lineDescription = string.IsNullOrWhiteSpace(line.Description) ? description : line.Description.Trim();
            var tracked = line.Tracking1Id is > 0 || line.Tracking2Id is > 0;
            var lastLineId = tracked
                ? await con.ExecuteScalarAsync<int>("SELECT ISNULL(MAX(iJournalLineID), 0) FROM tblJournalLines WHERE iJournalID = @id", new { id }, tx)
                : 0;
            if (line.TaxCodeId is { } taxCodeId)
            {
                // The proc splits the amount into the line before tax and a tax line to the control account
                var tax = line.TaxAmount(taxCodes);
                await con.ExecuteAsync("uspAddJournalLineWithTax", new
                {
                    iUserId = portfolioId,
                    iJournalID = id,
                    iAccountID = line.AccountId,
                    dblDebit = line.Debit ?? 0,
                    dblCredit = line.Credit ?? 0,
                    strDescription = lineDescription,
                    iTaxCodeID = taxCodeId,
                    dblTaxAmount = tax,
                }, tx, commandType: CommandType.StoredProcedure);
                expectedLines += tax == 0 ? 1 : 2;
                if (tracked)
                    await TagAsync(con, tx, portfolioId, id, lastLineId, line);
                continue;
            }
            await con.ExecuteAsync("uspAddJournalLine", new
            {
                iJournalID = id,
                iAccountID = line.AccountId,
                dblDebit = line.Debit ?? 0,
                dblCredit = line.Credit ?? 0,
                strDescription = lineDescription,
            }, tx, commandType: CommandType.StoredProcedure);
            expectedLines++;
            if (tracked)
                await TagAsync(con, tx, portfolioId, id, lastLineId, line);
        }

        var saved = await GetAsync(con, tx, portfolioId, id);
        if (saved is null || saved.Lines.Count != expectedLines || saved.TotalDebit != saved.TotalCredit)
            throw new InvalidOperationException($"Journal {id} did not save balanced; nothing was saved.");

        // e.g. a repeating journal's count moving on, so the journal and that change commit together
        if (alsoInTransaction is not null)
            await alsoInTransaction(con, tx, id);
        await tx.CommitAsync();
        return new JournalChangeResult(JournalChangeOutcome.Saved, id);
    }

    /// <summary>
    /// Deletes a manual journal of this portfolio with uspDeleteJournal (its lines go with it).
    /// Journals posted for trades, payouts, cash, imports or allocations are refused.
    /// </summary>
    public async Task<JournalChangeResult> DeleteAsync(int portfolioId, int journalId)
    {
        await using var con = db.Create();
        await con.OpenAsync();
        await using var tx = await con.BeginTransactionAsync();

        var existing = await GetAsync(con, tx, portfolioId, journalId);
        if (existing is null)
            return new JournalChangeResult(JournalChangeOutcome.NotFound);
        if (!existing.IsManual)
            return new JournalChangeResult(JournalChangeOutcome.NotManual, journalId);

        await con.ExecuteAsync("uspDeleteJournal", new { iUserId = portfolioId, iJournalID = journalId },
            tx, commandType: CommandType.StoredProcedure);
        await tx.CommitAsync();
        return new JournalChangeResult(JournalChangeOutcome.Saved, journalId);
    }
}
