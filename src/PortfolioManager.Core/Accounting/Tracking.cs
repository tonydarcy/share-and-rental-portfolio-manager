using System.Data;
using System.Globalization;
using System.Xml.Linq;
using Dapper;
using PortfolioManager.Core.Data;
using PortfolioManager.Core.Regions;
using PortfolioManager.Core.Tax;

namespace PortfolioManager.Core.Accounting;

public sealed record TrackingOption(int OptionId, int CategoryId, string Name, bool Active);

/// <summary>A tracking category (Department, Property, Project) in position 1 or 2, with its options.</summary>
public sealed record TrackingCategory(int CategoryId, int Position, string Name, bool Active, IReadOnlyList<TrackingOption> Options)
{
    public IEnumerable<TrackingOption> ActiveOptions => Options.Where(o => o.Active);
}

/// <summary>The portfolio's tracking categories; empty when it has none (or before the migration).</summary>
public sealed record TrackingSetup(IReadOnlyList<TrackingCategory> Categories)
{
    public static readonly TrackingSetup None = new([]);

    public TrackingCategory? First => Categories.FirstOrDefault(c => c.Position == 1 && c.Active);
    public TrackingCategory? Second => Categories.FirstOrDefault(c => c.Position == 2 && c.Active);
    public bool Any => First is not null || Second is not null;

    public IEnumerable<TrackingOption> AllOptions => Categories.SelectMany(c => c.Options);

    public string? OptionName(int? id) => id is null ? null : AllOptions.FirstOrDefault(o => o.OptionId == id)?.Name;

    /// <summary>"Sales / 12 Smith St" for a line's two options.</summary>
    public string Describe(int? option1, int? option2) =>
        string.Join(" / ", new[] { OptionName(option1), OptionName(option2) }.Where(n => n is not null));
}

/// <summary>One account's total for one tracking option (or none) over a period: income and expenses as positive amounts.</summary>
public sealed record TrackedAmount(int AccountId, int AccountNumber, string AccountName, string AccountType, int? OptionId, decimal Amount);

/// <summary>A profit and loss with a column for each option of a category, and one for lines with none.</summary>
public sealed record TrackingReport(TrackingCategory Category, DateOnly From, DateOnly To, IReadOnlyList<TrackedAmount> Amounts)
{
    public IReadOnlyList<int?> Columns =>
        [.. Category.Options.Where(o => o.Active || Amounts.Any(a => a.OptionId == o.OptionId)).Select(o => (int?)o.OptionId), null];

    public IEnumerable<(int AccountId, int Number, string Name, string Type)> Accounts(string type) =>
        Amounts.Where(a => a.AccountType == type).Select(a => (a.AccountId, a.AccountNumber, a.AccountName, a.AccountType)).Distinct().OrderBy(a => a.AccountNumber);

    public decimal Amount(int accountId, int? optionId) => Amounts.Where(a => a.AccountId == accountId && a.OptionId == optionId).Sum(a => a.Amount);

    public decimal Total(string type, int? optionId) => Amounts.Where(a => a.AccountType == type && a.OptionId == optionId).Sum(a => a.Amount);

    public decimal Profit(int? optionId) => Total("Revenue", optionId) - Total("Expense", optionId);
}

/// <summary>Tracking categories and options (2026-10-21-22-tracking-budgets.sql) and the profit and loss by option.</summary>
public sealed class TrackingService(IDbConnectionFactory db)
{
    public async Task<TrackingSetup> GetAsync(int portfolioId)
    {
        await using var con = db.Create();
        try
        {
            var cats = (await con.QueryAsync<(int Id, int Position, string Name, bool Active)>(
                "SELECT iCategoryID, iPosition, strName, bActive FROM tblTrackingCategories WHERE iUserID = @portfolioId ORDER BY iPosition",
                new { portfolioId })).ToList();
            var options = (await con.QueryAsync<(int Id, int CategoryId, string Name, bool Active)>("""
                SELECT O.iOptionID, O.iCategoryID, O.strName, O.bActive FROM tblTrackingOptions O
                JOIN tblTrackingCategories C ON C.iCategoryID = O.iCategoryID WHERE C.iUserID = @portfolioId ORDER BY O.strName
                """, new { portfolioId })).ToList();
            return new TrackingSetup(cats.Select(c => new TrackingCategory(c.Id, c.Position, c.Name, c.Active,
                options.Where(o => o.CategoryId == c.Id).Select(o => new TrackingOption(o.Id, o.CategoryId, o.Name, o.Active)).ToList())).ToList());
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208)
        {
            return TrackingSetup.None;
        }
    }

    public async Task<ProcResult> SaveCategoryAsync(int portfolioId, int categoryId, string? name, bool active)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 100)
            return ProcResult.Failed("Enter the category's name, up to 100 characters.");
        return await IdResultAsync("uspSaveTrackingCategory", new { iUserId = portfolioId, iCategoryID = categoryId, strName = name.Trim(), bActive = active });
    }

    public async Task<ProcResult> SaveOptionAsync(int portfolioId, int optionId, int categoryId, string? name, bool active)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 100)
            return ProcResult.Failed("Enter the option's name, up to 100 characters.");
        return await IdResultAsync("uspSaveTrackingOption",
            new { iUserId = portfolioId, iOptionID = optionId, iCategoryID = categoryId, strName = name.Trim(), bActive = active });
    }

    public async Task<ProcResult> DeleteOptionAsync(int portfolioId, int optionId)
    {
        await using var con = db.Create();
        return ProcResult.From(await con.ExecuteScalarAsync<string>("uspDeleteTrackingOption",
            new { iUserId = portfolioId, iOptionID = optionId }, commandType: CommandType.StoredProcedure), "Success");
    }

    /// <summary>Income and expense accounts by tracking option of one category (position 1 or 2) over the period.</summary>
    public async Task<IReadOnlyList<TrackedAmount>> GetAmountsAsync(int portfolioId, int position, DateOnly from, DateOnly to)
    {
        var column = position == 2 ? "iTrackingOption2ID" : "iTrackingOption1ID";
        await using var con = db.Create();
        var rows = await con.QueryAsync<(int AccountId, int Number, string Name, string Type, int? OptionId, decimal Amount)>($"""
            SELECT A.iAccountID, A.iAccountNumber, A.strAccountName, A.strAccountType, L.{column},
                   SUM(CASE WHEN A.strAccountType = 'Revenue' THEN ISNULL(L.dblCredit, 0) - ISNULL(L.dblDebit, 0)
                            ELSE ISNULL(L.dblDebit, 0) - ISNULL(L.dblCredit, 0) END)
            FROM tblJournalLines L
            JOIN tblJournal J ON J.iJournalID = L.iJournalID
            JOIN tblChartOfAccounts A ON A.iAccountID = L.iAccountID
            WHERE J.iUserID = @portfolioId AND J.dtmTransactionDate BETWEEN @from AND @to AND A.strAccountType IN ('Revenue', 'Expense')
            GROUP BY A.iAccountID, A.iAccountNumber, A.strAccountName, A.strAccountType, L.{column}
            """, new { portfolioId, from = from.ToDateTime(TimeOnly.MinValue), to = to.ToDateTime(TimeOnly.MinValue) });
        return rows.Where(r => r.Amount != 0).Select(r => new TrackedAmount(r.AccountId, r.Number, r.Name, r.Type, r.OptionId, r.Amount)).ToList();
    }

    private async Task<ProcResult> IdResultAsync(string proc, object args)
    {
        await using var con = db.Create();
        var row = await con.QuerySingleAsync<(string Result, int? Id)>(proc, args, commandType: CommandType.StoredProcedure);
        return ProcResult.From(row.Result, "Success", row.Id);
    }
}

public sealed record Budget(int BudgetId, string Name, int Year, int? TrackingOptionId, DateTime Updated);

/// <summary>A budget's amounts: account → twelve months (index 0 is the first month of the financial year).</summary>
public sealed record BudgetAmounts(IReadOnlyDictionary<int, decimal[]> ByAccount)
{
    public decimal Month(int accountId, int month) => ByAccount.TryGetValue(accountId, out var m) ? m[month] : 0;
    public decimal Year(int accountId) => ByAccount.TryGetValue(accountId, out var m) ? m.Sum() : 0;
}

/// <summary>Budget against actual for one account: twelve months each.</summary>
public sealed record BudgetLine(int AccountId, int Number, string Name, string Type, decimal[] Budget, decimal[] Actual)
{
    public decimal BudgetTotal => Budget.Sum();
    public decimal ActualTotal => Actual.Sum();

    /// <summary>Better (positive) or worse than budget: more income, or less spending, is better.</summary>
    public decimal Variance(decimal budget, decimal actual) => Type == "Revenue" ? actual - budget : budget - actual;
}

public static class BudgetRules
{
    /// <summary>The first day of each of the financial year's twelve months.</summary>
    public static IReadOnlyList<DateOnly> Months(int year, YearStart? start = null)
    {
        var first = FinancialYear.StartDate(year, start);
        return Enumerable.Range(0, 12).Select(first.AddMonths).ToList();
    }

    /// <summary>A year's amount spread evenly over twelve months, to the cent, with the rounding in the last month.</summary>
    public static decimal[] Spread(decimal yearly)
    {
        var each = Math.Round(yearly / 12m, 2, MidpointRounding.AwayFromZero);
        var months = Enumerable.Repeat(each, 12).ToArray();
        months[11] = yearly - each * 11;
        return months;
    }

    public static string LinesXml(IReadOnlyDictionary<int, decimal[]> amounts) =>
        new XElement("lines", amounts.SelectMany(a => a.Value.Select((v, i) => (a.Key, Month: i + 1, Value: v)))
            .Where(x => x.Value != 0)
            .Select(x => new XElement("b", new XAttribute("a", x.Key), new XAttribute("m", x.Month),
                new XAttribute("v", x.Value.ToString(CultureInfo.InvariantCulture))))).ToString(SaveOptions.DisableFormatting);
}

/// <summary>Budgets by account and month, and budget against actual (2026-10-21-22-tracking-budgets.sql).</summary>
public sealed class BudgetService(IDbConnectionFactory db)
{
    public async Task<IReadOnlyList<Budget>> GetBudgetsAsync(int portfolioId)
    {
        await using var con = db.Create();
        try
        {
            return (await con.QueryAsync<(int Id, string Name, int Year, int? Option, DateTime Updated)>(
                    "SELECT iBudgetID, strName, iYear, iTrackingOptionID, dtmUpdatedDate FROM tblBudgets WHERE iUserID = @portfolioId ORDER BY iYear DESC, strName",
                    new { portfolioId }))
                .Select(b => new Budget(b.Id, b.Name, b.Year, b.Option, b.Updated)).ToList();
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208)
        {
            return [];
        }
    }

    public async Task<BudgetAmounts> GetAmountsAsync(int portfolioId, int budgetId)
    {
        await using var con = db.Create();
        var rows = await con.QueryAsync<(int Account, int Month, decimal Amount)>("""
            SELECT L.iAccountID, L.iMonth, L.dblAmount FROM tblBudgetLines L JOIN tblBudgets B ON B.iBudgetID = L.iBudgetID
            WHERE L.iBudgetID = @budgetId AND B.iUserID = @portfolioId
            """, new { portfolioId, budgetId });
        var map = new Dictionary<int, decimal[]>();
        foreach (var r in rows)
        {
            if (!map.TryGetValue(r.Account, out var m))
                map[r.Account] = m = new decimal[12];
            if (r.Month is >= 1 and <= 12)
                m[r.Month - 1] = r.Amount;
        }
        return new BudgetAmounts(map);
    }

    public async Task<ProcResult> SaveBudgetAsync(int portfolioId, int budgetId, string? name, int year, int? trackingOptionId)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 100)
            return ProcResult.Failed("Enter a name for the budget, up to 100 characters.");
        await using var con = db.Create();
        var row = await con.QuerySingleAsync<(string Result, int? Id)>("uspSaveBudget",
            new { iUserId = portfolioId, iBudgetID = budgetId, strName = name.Trim(), iYear = year, iTrackingOptionID = trackingOptionId },
            commandType: CommandType.StoredProcedure);
        return ProcResult.From(row.Result, "Success", row.Id);
    }

    public async Task<ProcResult> SaveAmountsAsync(int portfolioId, int budgetId, IReadOnlyDictionary<int, decimal[]> amounts)
    {
        await using var con = db.Create();
        return ProcResult.From(await con.ExecuteScalarAsync<string>("uspSaveBudgetLines",
            new { iUserId = portfolioId, iBudgetID = budgetId, xmlLines = BudgetRules.LinesXml(amounts) }, commandType: CommandType.StoredProcedure), "Success");
    }

    public async Task<ProcResult> DeleteAsync(int portfolioId, int budgetId)
    {
        await using var con = db.Create();
        return ProcResult.From(await con.ExecuteScalarAsync<string>("uspDeleteBudget",
            new { iUserId = portfolioId, iBudgetID = budgetId }, commandType: CommandType.StoredProcedure), "Success");
    }

    /// <summary>
    /// Each income and expense account's budget and actual for the twelve months of the budget's year. With a tracking
    /// option on the budget, only journal lines with that option count as actual.
    /// </summary>
    public async Task<IReadOnlyList<BudgetLine>> CompareAsync(int portfolioId, Budget budget, YearStart? start = null)
    {
        var months = BudgetRules.Months(budget.Year, start);
        var from = months[0];
        var to = FinancialYear.EndDate(budget.Year, start);
        var amounts = await GetAmountsAsync(portfolioId, budget.BudgetId);
        await using var con = db.Create();
        var actualRows = await con.QueryAsync<(int AccountId, int Number, string Name, string Type, DateTime Month, decimal Amount)>("""
            SELECT A.iAccountID, A.iAccountNumber, A.strAccountName, A.strAccountType,
                   DATEFROMPARTS(YEAR(J.dtmTransactionDate), MONTH(J.dtmTransactionDate), 1),
                   SUM(CASE WHEN A.strAccountType = 'Revenue' THEN ISNULL(L.dblCredit, 0) - ISNULL(L.dblDebit, 0)
                            ELSE ISNULL(L.dblDebit, 0) - ISNULL(L.dblCredit, 0) END)
            FROM tblJournalLines L
            JOIN tblJournal J ON J.iJournalID = L.iJournalID
            JOIN tblChartOfAccounts A ON A.iAccountID = L.iAccountID
            WHERE J.iUserID = @portfolioId AND J.dtmTransactionDate BETWEEN @from AND @to AND A.strAccountType IN ('Revenue', 'Expense')
              AND (@option IS NULL OR L.iTrackingOption1ID = @option OR L.iTrackingOption2ID = @option)
            GROUP BY A.iAccountID, A.iAccountNumber, A.strAccountName, A.strAccountType,
                     DATEFROMPARTS(YEAR(J.dtmTransactionDate), MONTH(J.dtmTransactionDate), 1)
            """, new { portfolioId, from = from.ToDateTime(TimeOnly.MinValue), to = to.ToDateTime(TimeOnly.MinValue), option = budget.TrackingOptionId });
        var budgetAccounts = amounts.ByAccount.Keys.ToList();
        var names = budgetAccounts.Count == 0
            ? new Dictionary<int, (int Number, string Name, string Type)>()
            : (await con.QueryAsync<(int Id, int Number, string Name, string Type)>(
                    "SELECT iAccountID, iAccountNumber, strAccountName, strAccountType FROM tblChartOfAccounts WHERE iAccountID IN @budgetAccounts",
                    new { budgetAccounts }))
                .ToDictionary(a => a.Id, a => (a.Number, a.Name, a.Type));
        return Compare(months, amounts, actualRows.Select(r => (r.AccountId, r.Number, r.Name, r.Type, DateOnly.FromDateTime(r.Month), r.Amount)), names);
    }

    public static IReadOnlyList<BudgetLine> Compare(IReadOnlyList<DateOnly> months, BudgetAmounts budget,
        IEnumerable<(int AccountId, int Number, string Name, string Type, DateOnly Month, decimal Amount)> actuals,
        IReadOnlyDictionary<int, (int Number, string Name, string Type)> budgetAccountNames)
    {
        var lines = new Dictionary<int, BudgetLine>();
        BudgetLine Line(int id, int number, string name, string type) =>
            lines.TryGetValue(id, out var l) ? l : lines[id] = new BudgetLine(id, number, name, type, budget.ByAccount.TryGetValue(id, out var b) ? b : new decimal[12], new decimal[12]);
        foreach (var a in actuals)
        {
            var index = months.ToList().FindIndex(m => m.Year == a.Month.Year && m.Month == a.Month.Month);
            if (index >= 0)
                Line(a.AccountId, a.Number, a.Name, a.Type).Actual[index] += a.Amount;
        }
        foreach (var (id, info) in budgetAccountNames)
            Line(id, info.Number, info.Name, info.Type);
        return lines.Values.OrderBy(l => l.Type == "Revenue" ? 0 : 1).ThenBy(l => l.Number).ToList();
    }
}
