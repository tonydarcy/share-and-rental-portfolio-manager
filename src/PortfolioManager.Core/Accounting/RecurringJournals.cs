using System.Data;
using System.Globalization;
using System.Xml.Linq;
using Dapper;
using PortfolioManager.Core.Data;

namespace PortfolioManager.Core.Accounting;

public static class RepeatFrequencies
{
    public const string Weekly = "Weekly", Fortnightly = "Fortnightly", Monthly = "Monthly", Quarterly = "Quarterly",
        HalfYearly = "HalfYearly", Yearly = "Yearly";

    public static readonly IReadOnlyList<string> All = [Weekly, Fortnightly, Monthly, Quarterly, HalfYearly, Yearly];

    public static string Label(string f) => f switch
    {
        HalfYearly => "Every six months",
        Fortnightly => "Fortnightly",
        _ => f,
    };

    /// <summary>
    /// The nth date (0 is the start). Months count from the start date, so a journal on the 31st stays at the
    /// end of each month instead of drifting to the 28th after February.
    /// </summary>
    public static DateOnly Occurrence(DateOnly start, string frequency, int n) => frequency switch
    {
        Weekly => start.AddDays(7 * n),
        Fortnightly => start.AddDays(14 * n),
        Quarterly => start.AddMonths(3 * n),
        HalfYearly => start.AddMonths(6 * n),
        Yearly => start.AddYears(n),
        _ => start.AddMonths(n),
    };
}

/// <summary>
/// A journal that is posted again on a schedule (rent, fees, depreciation), and optionally reversed the next day
/// (an accrual or prepayment at month end that unwinds at the start of the next).
/// </summary>
public sealed record RecurringJournal(
    int Id,
    string Name,
    string Description,
    string Frequency,
    DateOnly Start,
    DateOnly? End,
    int PostedCount,
    bool Reverse,
    bool Active,
    IReadOnlyList<JournalLineInput> Lines,
    DateOnly? LastPosted)
{
    public DateOnly NextDate => RepeatFrequencies.Occurrence(Start, Frequency, PostedCount);

    public bool Finished => End is { } end && NextDate > end;

    /// <summary>The dates still to post up to and including <paramref name="asAt"/>, at most <paramref name="max"/> of them.</summary>
    public IReadOnlyList<DateOnly> DueDates(DateOnly asAt, int max = 60)
    {
        var dates = new List<DateOnly>();
        if (!Active)
            return dates;
        for (var n = PostedCount; dates.Count < max; n++)
        {
            var d = RepeatFrequencies.Occurrence(Start, Frequency, n);
            if (d > asAt || (End is { } end && d > end))
                break;
            dates.Add(d);
        }
        return dates;
    }

    public JournalInput ToInput(DateOnly date) => new()
    {
        Date = date.ToDateTime(TimeOnly.MinValue),
        Description = Description,
        Lines = Lines.Select(Copy).ToList(),
    };

    private static JournalLineInput Copy(JournalLineInput l) => new()
    {
        AccountId = l.AccountId, Description = l.Description, Debit = l.Debit, Credit = l.Credit, TaxCodeId = l.TaxCodeId, Tax = l.Tax,
    };
}

public static class JournalReversal
{
    /// <summary>The same lines with debits and credits swapped, dated <paramref name="date"/>.</summary>
    public static JournalInput Reverse(JournalInput original, DateOnly date) => new()
    {
        Date = date.ToDateTime(TimeOnly.MinValue),
        Description = Describe(original.Description),
        Lines = original.UsedLines.Select(l => new JournalLineInput
        {
            AccountId = l.AccountId,
            Description = l.Description,
            Debit = l.Credit,
            Credit = l.Debit,
            TaxCodeId = l.TaxCodeId,
            Tax = l.Tax,
            Tracking1Id = l.Tracking1Id,
            Tracking2Id = l.Tracking2Id,
        }).ToList(),
    };

    public static string Describe(string description)
    {
        var text = "Reversal of " + description.Trim();
        return text.Length > 500 ? text[..500] : text;
    }

    /// <summary>The lines as stored with a repeating journal (the database is on a version without JSON).</summary>
    public static string LinesXml(IEnumerable<JournalLineInput> lines) =>
        new XElement("lines", lines.Where(l => !l.IsBlank).Select(l => new XElement("l",
            new XAttribute("a", l.AccountId),
            l.Debit is { } d ? new XAttribute("d", d.ToString(CultureInfo.InvariantCulture)) : null,
            l.Credit is { } c ? new XAttribute("c", c.ToString(CultureInfo.InvariantCulture)) : null,
            l.TaxCodeId is { } t ? new XAttribute("t", t) : null,
            l.Tax is { } x ? new XAttribute("x", x.ToString(CultureInfo.InvariantCulture)) : null,
            l.Tracking1Id is > 0 ? new XAttribute("t1", l.Tracking1Id) : null,
            l.Tracking2Id is > 0 ? new XAttribute("t2", l.Tracking2Id) : null,
            string.IsNullOrWhiteSpace(l.Description) ? null : new XAttribute("n", l.Description.Trim())))).ToString(SaveOptions.DisableFormatting);

    public static IReadOnlyList<JournalLineInput> ParseLines(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
            return [];
        static decimal? Dec(XElement e, string name) =>
            e.Attribute(name)?.Value is { } v && decimal.TryParse(v, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : null;
        return XElement.Parse(xml).Elements("l").Select(e => new JournalLineInput
        {
            AccountId = (int?)e.Attribute("a") ?? 0,
            Debit = Dec(e, "d"),
            Credit = Dec(e, "c"),
            TaxCodeId = (int?)e.Attribute("t"),
            Tax = Dec(e, "x"),
            Tracking1Id = (int?)e.Attribute("t1"),
            Tracking2Id = (int?)e.Attribute("t2"),
            Description = e.Attribute("n")?.Value,
        }).ToList();
    }
}

public sealed class RecurringJournalInput
{
    public string Name { get; set; } = "";
    public string Frequency { get; set; } = RepeatFrequencies.Monthly;
    public DateTime? End { get; set; }
    public bool Reverse { get; set; }

    public IReadOnlyList<string> Validate(DateOnly start)
    {
        var errors = new List<string>();
        if (!RepeatFrequencies.All.Contains(Frequency))
            errors.Add("Choose how often the journal repeats.");
        if (End is { } end && DateOnly.FromDateTime(end) < start)
            errors.Add("The last date can't be before the first.");
        if ((Name?.Trim().Length ?? 0) > 200)
            errors.Add("The name can be at most 200 characters.");
        return errors;
    }
}

public sealed record PostedJournals(int Posted, IReadOnlyList<string> Errors);

/// <summary>tblRecurringJournals (2026-10-21-14-recurring-journals.sql). The journals themselves are posted by <see cref="JournalService"/>.</summary>
public sealed class RecurringJournalService(IDbConnectionFactory db, JournalService journals)
{
    private sealed record Row(int Id, string Name, string Description, string Frequency, DateTime Start, DateTime? End, int PostedCount,
        bool Reverse, bool Active, string LinesXml, DateTime? LastPosted);

    /// <summary>Empty until the migration has been run.</summary>
    public async Task<IReadOnlyList<RecurringJournal>> ListAsync(int portfolioId)
    {
        await using var con = db.Create();
        try
        {
            var rows = await con.QueryAsync<Row>("""
                SELECT iRecurringJournalID AS Id, strName AS Name, strDescription AS Description, strFrequency AS Frequency,
                       CAST(dtmStart AS datetime) AS Start, CAST(dtmEnd AS datetime) AS [End], iPostedCount AS PostedCount, bReverse AS Reverse,
                       bActive AS Active, strLinesXml AS LinesXml, CAST(dtmLastPosted AS datetime) AS LastPosted
                FROM tblRecurringJournals WHERE iUserID = @portfolioId ORDER BY strName
                """, new { portfolioId });
            return rows.Select(Map).ToList();
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208)
        {
            return [];
        }
    }

    private static RecurringJournal Map(Row r) => new(r.Id, r.Name, r.Description, r.Frequency, DateOnly.FromDateTime(r.Start),
        r.End is { } e ? DateOnly.FromDateTime(e) : null, r.PostedCount, r.Reverse, r.Active, JournalReversal.ParseLines(r.LinesXml),
        r.LastPosted is { } l ? DateOnly.FromDateTime(l) : null);

    public async Task<RecurringJournal?> GetAsync(int portfolioId, int id) => (await ListAsync(portfolioId)).FirstOrDefault(r => r.Id == id);

    /// <summary>
    /// Posts the first journal now and keeps it as a repeating journal (posted once), in one transaction.
    /// With Reverse, each posting is reversed the next day.
    /// </summary>
    public async Task<JournalChangeResult> PostAndRepeatAsync(int portfolioId, JournalInput input, RecurringJournalInput repeat)
    {
        var start = DateOnly.FromDateTime(input.Date);
        if (repeat.Validate(start) is { Count: > 0 } errors)
            return new JournalChangeResult(JournalChangeOutcome.Invalid, null, errors);
        var result = await journals.SaveAsync(portfolioId, null, input, async (con, tx, _) =>
        {
            var row = await con.QuerySingleAsync<(string Result, int? Id)>("uspSaveRecurringJournal",
                SaveParameters(portfolioId, 0, input, repeat, postedCount: 1), tx, commandType: CommandType.StoredProcedure);
            if (!ProcResult.From(row.Result, "Success").Ok)
                throw new InvalidOperationException(row.Result);
        });
        if (result.Outcome == JournalChangeOutcome.Saved && repeat.Reverse)
            await journals.SaveAsync(portfolioId, null, JournalReversal.Reverse(input, start.AddDays(1)));
        return result;
    }

    /// <summary>Changes a repeating journal's lines and schedule without posting anything.</summary>
    public async Task<ProcResult> UpdateAsync(int portfolioId, int id, JournalInput input, RecurringJournalInput repeat,
        IReadOnlySet<int> allowedAccounts, IReadOnlyDictionary<int, SalesTax.TaxCode> taxCodes)
    {
        var errors = input.Validate(allowedAccounts, taxCodes).Concat(repeat.Validate(DateOnly.FromDateTime(input.Date))).ToList();
        if (errors.Count > 0)
            return ProcResult.Failed(string.Join(" ", errors));
        await using var con = db.Create();
        var row = await con.QuerySingleAsync<(string Result, int? Id)>("uspSaveRecurringJournal",
            SaveParameters(portfolioId, id, input, repeat, postedCount: null), commandType: CommandType.StoredProcedure);
        return ProcResult.From(row.Result, "Success", row.Id);
    }

    public static object SaveParameters(int portfolioId, int id, JournalInput input, RecurringJournalInput repeat, int? postedCount) => new
    {
        iUserID = portfolioId,
        iRecurringJournalID = id,
        strName = string.IsNullOrWhiteSpace(repeat.Name) ? input.Description.Trim() : repeat.Name.Trim(),
        strDescription = input.Description.Trim(),
        strFrequency = repeat.Frequency,
        dtmStart = input.Date.Date,
        dtmEnd = repeat.End?.Date,
        bReverse = repeat.Reverse,
        strLinesXml = JournalReversal.LinesXml(input.Lines),
        iPostedCount = postedCount,
    };

    public async Task<ProcResult> SetActiveAsync(int portfolioId, int id, bool active) =>
        await ResultAsync("uspSetRecurringJournalActive", new { iUserID = portfolioId, iRecurringJournalID = id, bActive = active });

    public async Task<ProcResult> DeleteAsync(int portfolioId, int id) =>
        await ResultAsync("uspDeleteRecurringJournal", new { iUserID = portfolioId, iRecurringJournalID = id });

    /// <summary>
    /// Posts every repeating journal that is due up to <paramref name="asAt"/>. Each posting and the count moving on
    /// commit together, so a journal is never posted twice; its reversal follows. Stops a journal at its first error.
    /// </summary>
    public async Task<PostedJournals> PostDueAsync(int portfolioId, DateOnly asAt)
    {
        var posted = 0;
        var errors = new List<string>();
        foreach (var r in await ListAsync(portfolioId))
        {
            var count = r.PostedCount;
            foreach (var date in r.DueDates(asAt))
            {
                var input = r.ToInput(date);
                var expected = count;
                JournalChangeResult result;
                try
                {
                    result = await journals.SaveAsync(portfolioId, null, input, async (con, tx, _) =>
                    {
                        var ok = await con.ExecuteScalarAsync<string>("uspMarkRecurringJournalPosted",
                            new { iUserID = portfolioId, iRecurringJournalID = r.Id, iPostedCount = expected, dtmPosted = date.ToDateTime(TimeOnly.MinValue) },
                            tx, commandType: CommandType.StoredProcedure);
                        if (!ProcResult.From(ok, "Success").Ok)
                            throw new InvalidOperationException($"{r.Name} was posted by someone else at the same time.");
                    });
                }
                catch (Exception ex)
                {
                    errors.Add($"{r.Name} on {date:d}: {(LockedPeriodService.IsLockError(ex) ? "that date is in a locked period." : ex.Message)}");
                    break;
                }
                if (result.Outcome != JournalChangeOutcome.Saved)
                {
                    errors.Add($"{r.Name}: {string.Join(" ", result.Errors ?? ["it couldn't be posted."])}");
                    break;
                }
                count++;
                posted++;
                if (r.Reverse)
                    await journals.SaveAsync(portfolioId, null, JournalReversal.Reverse(input, date.AddDays(1)));
            }
        }
        return new PostedJournals(posted, errors);
    }

    /// <summary>Posts a reversal of one of the portfolio's manual journals on <paramref name="date"/>.</summary>
    public async Task<JournalChangeResult> ReverseAsync(int portfolioId, int journalId, DateOnly date, IReadOnlyDictionary<int, SalesTax.TaxCode> taxCodes)
    {
        var journal = await journals.GetAsync(portfolioId, journalId);
        if (journal is null)
            return new JournalChangeResult(JournalChangeOutcome.NotFound);
        if (!journal.IsManual)
            return new JournalChangeResult(JournalChangeOutcome.NotManual, journalId);
        return await journals.SaveAsync(portfolioId, null, JournalReversal.Reverse(JournalService.ToInput(journal, taxCodes), date));
    }

    private async Task<ProcResult> ResultAsync(string proc, object args)
    {
        await using var con = db.Create();
        return ProcResult.From(await con.ExecuteScalarAsync<string>(proc, args, commandType: CommandType.StoredProcedure), "Success");
    }
}
