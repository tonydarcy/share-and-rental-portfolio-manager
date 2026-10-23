using System.Data;
using Dapper;
using PortfolioManager.Core.Data;
using PortfolioManager.Core.Email;

namespace PortfolioManager.Core.Alerts;

/// <summary>Which emails one login gets about one portfolio.</summary>
public sealed record AlertSettings(bool PriceTargets, bool Dividends, bool SyncFailures, bool Unallocated, bool Property = true)
{
    public static readonly AlertSettings Default = new(true, true, true, false);
}

public sealed record PriceAlert(int PriceAlertId, string ExchangeCode, string StockCode, string Direction, decimal Target, string? Note,
    DateTime Created, DateTime? Sent, decimal? PriceSent);

/// <summary>One line of an alert email.</summary>
public sealed record AlertItem(string Kind, string Text, int? PriceAlertId = null, decimal? Price = null, string? Key = null);

/// <summary>One login's alerts for one portfolio, ready to email.</summary>
public sealed record AlertDigest(int LoginId, int PortfolioId, string Email, string PortfolioName, IReadOnlyList<AlertItem> Items)
{
    public bool HasUnallocated => Items.Any(i => i.Kind == AlertKinds.Unallocated);
}

public static class AlertKinds
{
    public const string Price = "Price", Dividend = "Dividend", Sync = "Sync", Unallocated = "Unallocated", Property = "Property";

    /// <summary>A price target is reached when the close is at or above (or at or below) it.</summary>
    public static bool Reached(string direction, decimal target, decimal close) =>
        direction.Equals("Below", StringComparison.OrdinalIgnoreCase) ? close <= target : close >= target;

    public static EmailMessage Email(AlertDigest d, string siteName, string link)
    {
        var lines = new List<string> { $"News about \"{d.PortfolioName}\" on {siteName}:" };
        lines.AddRange(d.Items.Select(i => "• " + i.Text));
        lines.Add("You can change which alerts you get on the Alerts page (account menu).");
        var subject = d.Items.Count == 1 ? $"{d.PortfolioName}: {d.Items[0].Text}" : $"{d.PortfolioName}: {d.Items.Count} alerts";
        if (subject.Length > 150) subject = subject[..147] + "...";
        return EmailTemplates.Simple(d.Email, subject, lines, $"Open {siteName}", link);
    }
}

/// <summary>
/// Email alerts: price targets reached, dividends announced for a holding, Navexa sync failures and bank lines waiting to be
/// allocated, one digest per login and portfolio. Settings and targets are written through the procs in 2026-10-21-08-alerts.sql.
/// </summary>
public sealed class AlertService(IDbConnectionFactory db)
{
    private readonly Rentals.PropertyService properties = new(db);

    /// <summary>Missed rent, leases ending, rent reviews and checks due on the portfolio's rental properties, each emailed once.</summary>
    private async Task<IEnumerable<AlertItem>> PropertyItemsAsync(int portfolioId, DateOnly today)
    {
        var list = await new TaxWork.RentalService(db).GetPropertiesAsync(portfolioId);
        if (list.Count == 0)
            return [];
        var book = await properties.GetAsync(portfolioId);
        var sent = await properties.GetSentAlertsAsync(portfolioId);
        return Rentals.PropertyReminders.Due(list.Where(p => p.Active), book.Tenancies, book.Payments, book.Compliance, today)
            .Where(r => !sent.Contains(r.Key))
            .Select(r => new AlertItem(AlertKinds.Property, $"{r.Address}: {r.Text}", Key: r.Key));
    }

    public async Task<AlertSettings> GetSettingsAsync(int loginId, int portfolioId)
    {
        await using var con = db.Create();
        try
        {
            var row = await con.QuerySingleOrDefaultAsync<(bool, bool, bool, bool, bool)?>(
                "SELECT bPriceTargets, bDividends, bSyncFailures, bUnallocated, bProperty FROM tblAlertSettings WHERE iLoginID = @loginId AND iPortfolioID = @portfolioId",
                new { loginId, portfolioId });
            return row is { } r ? new AlertSettings(r.Item1, r.Item2, r.Item3, r.Item4, r.Item5) : AlertSettings.Default;
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 207)
        {
            // Before 2026-10-23-02-running-a-property.sql
            var row = await con.QuerySingleOrDefaultAsync<(bool, bool, bool, bool)?>(
                "SELECT bPriceTargets, bDividends, bSyncFailures, bUnallocated FROM tblAlertSettings WHERE iLoginID = @loginId AND iPortfolioID = @portfolioId",
                new { loginId, portfolioId });
            return row is { } r ? new AlertSettings(r.Item1, r.Item2, r.Item3, r.Item4) : AlertSettings.Default;
        }
    }

    public async Task SaveSettingsAsync(int loginId, int portfolioId, AlertSettings s)
    {
        await using var con = db.Create();
        await con.ExecuteAsync("uspSaveAlertSettings", new
        {
            iLoginID = loginId, iPortfolioID = portfolioId,
            bPriceTargets = s.PriceTargets, bDividends = s.Dividends, bSyncFailures = s.SyncFailures, bUnallocated = s.Unallocated, bProperty = s.Property,
        }, commandType: CommandType.StoredProcedure);
    }

    public async Task<IReadOnlyList<PriceAlert>> GetPriceAlertsAsync(int loginId, int portfolioId)
    {
        await using var con = db.Create();
        var rows = await con.QueryAsync<(int, string, string, string, double, string?, DateTime, DateTime?, double?)>("""
            SELECT iPriceAlertID, strStockExchangeCode, strStockCode, strDirection, dblTarget, strNote, dtmCreated, dtmSent, dblPriceSent
            FROM tblPriceAlerts WHERE iLoginID = @loginId AND iPortfolioID = @portfolioId
            ORDER BY CASE WHEN dtmSent IS NULL THEN 0 ELSE 1 END, strStockCode
            """, new { loginId, portfolioId });
        return rows.Select(r => new PriceAlert(r.Item1, r.Item2, r.Item3, r.Item4, (decimal)r.Item5, r.Item6, r.Item7, r.Item8, (decimal?)r.Item9)).ToList();
    }

    public async Task<ProcResult> AddPriceAlertAsync(int loginId, int portfolioId, string market, string code, string direction, decimal target, string? note)
    {
        await using var con = db.Create();
        var result = await con.ExecuteScalarAsync<string>("uspSavePriceAlert", new
        {
            iLoginID = loginId, iPortfolioID = portfolioId, strStockExchangeCode = market, strStockCode = code,
            strDirection = direction, dblTarget = (double)target, strNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
        }, commandType: CommandType.StoredProcedure);
        return ProcResult.From(result, "Success");
    }

    public async Task<ProcResult> DeletePriceAlertAsync(int loginId, int priceAlertId)
    {
        await using var con = db.Create();
        var result = await con.ExecuteScalarAsync<string>("uspDeletePriceAlert", new { iLoginID = loginId, iPriceAlertID = priceAlertId },
            commandType: CommandType.StoredProcedure);
        return ProcResult.From(result, "Success");
    }

    private sealed record Subscriber(int LoginId, int PortfolioId, string Email, string PortfolioName, bool PriceTargets, bool Dividends,
        bool SyncFailures, bool Unallocated, DateTime LastChecked, DateTime? UnallocatedSent, bool Property);

    /// <summary>What each login has waiting since it was last checked. Only logins that can still open the portfolio.</summary>
    public async Task<IReadOnlyList<AlertDigest>> GetDueAsync(DateTime utcNow)
    {
        await using var con = db.Create();
        var subscribers = await con.QueryAsync<Subscriber>("""
            SELECT S.iLoginID AS LoginId, S.iPortfolioID AS PortfolioId, L.strLoginEmail AS Email, P.strEntityName AS PortfolioName,
                   S.bPriceTargets AS PriceTargets, S.bDividends AS Dividends, S.bSyncFailures AS SyncFailures, S.bUnallocated AS Unallocated,
                   S.dtmLastChecked AS LastChecked, CAST(S.dtmUnallocatedSent AS datetime) AS UnallocatedSent, S.bProperty AS Property
            FROM tblAlertSettings S
            JOIN tblUsers L ON L.iUserId = S.iLoginID
            JOIN tblUsers P ON P.iUserId = S.iPortfolioID
            WHERE ISNULL(L.strLoginEmail, '') LIKE '%@%'
              AND (S.iLoginID = S.iPortfolioID OR EXISTS (SELECT 1 FROM tblPortfolioPermissions X
                    WHERE X.iUserID = S.iLoginID AND X.iUserIDForPortfolioToAccess IN (S.iPortfolioID, 0)))
            """);

        var digests = new List<AlertDigest>();
        foreach (var s in subscribers)
        {
            var items = new List<AlertItem>();
            if (s.PriceTargets)
            {
                var hits = await con.QueryAsync<(int Id, string Code, string Direction, double Target, string? Note, double Close, DateTime Date)>("""
                    SELECT A.iPriceAlertID, A.strStockCode, A.strDirection, A.dblTarget, A.strNote, C.dblClosePrice, C.dtmDetailsAsAtDate
                    FROM tblPriceAlerts A
                    CROSS APPLY (SELECT TOP 1 dblClosePrice, dtmDetailsAsAtDate FROM tblSharePriceCache P
                                 WHERE P.strStockExchangeCode = A.strStockExchangeCode AND P.strStockCode = A.strStockCode AND P.dblClosePrice > 0
                                   AND (P.iUserID IS NULL OR P.iUserID = 0 OR P.iUserID = A.iPortfolioID)
                                 ORDER BY P.dtmDetailsAsAtDate DESC) C
                    WHERE A.iLoginID = @LoginId AND A.iPortfolioID = @PortfolioId AND A.dtmSent IS NULL AND C.dtmDetailsAsAtDate >= CAST(A.dtmCreated AS DATE)
                    """, new { s.LoginId, s.PortfolioId });
                foreach (var h in hits.Where(h => AlertKinds.Reached(h.Direction, (decimal)h.Target, (decimal)h.Close)))
                    items.Add(new AlertItem(AlertKinds.Price,
                        $"{h.Code} closed at {h.Close:0.00##} on {h.Date:d MMM yyyy}, {(h.Direction == "Below" ? "at or below" : "at or above")} your target of {h.Target:0.00##}" +
                        (string.IsNullOrWhiteSpace(h.Note) ? "." : $" ({h.Note})."), h.Id, (decimal)h.Close));
            }
            if (s.Dividends)
            {
                try
                {
                    var divs = await con.QueryAsync<(string Code, DateTime ExDate, DateTime? PayDate, double Amount, string? Currency)>("""
                        SELECT D.strStockCode, CAST(D.dtmExDate AS datetime), CAST(D.dtmPaymentDate AS datetime), D.dblAmountPerUnit, D.strCurrency
                        FROM tblAnnouncedDividends D
                        WHERE D.dtmFetchedDate > DATEADD(minute, DATEDIFF(minute, GETUTCDATE(), GETDATE()), @LastChecked)
                          AND D.dtmExDate >= DATEADD(day, -14, CAST(GETDATE() AS DATE))
                          AND (SELECT SUM(CASE WHEN UPPER(T.strType) = 'BUY' THEN T.iQuantity WHEN UPPER(T.strType) = 'SELL' THEN -T.iQuantity ELSE 0 END)
                                 FROM tblShareTrades T WHERE T.iUserId = @PortfolioId
                                  AND T.strStockExchangeCode = D.strStockExchangeCode AND T.strStockCode = D.strStockCode) > 0
                        ORDER BY D.dtmExDate
                        """, new { s.LastChecked, s.PortfolioId });
                    foreach (var d in divs)
                        items.Add(new AlertItem(AlertKinds.Dividend,
                            $"{d.Code} dividend of {d.Amount:0.00####} {d.Currency} a unit, ex {d.ExDate:d MMM yyyy}" +
                            (d.PayDate is { } p ? $", paid {p:d MMM yyyy}." : ".")));
                }
                catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { }
            }
            if (s.SyncFailures)
            {
                try
                {
                    var failed = await con.QuerySingleOrDefaultAsync<string?>(
                        "SELECT strLastResult FROM tblNavexaConnection WHERE iUserId = @PortfolioId AND dtmLastChecked > @LastChecked AND strLastResult LIKE 'Sync failed%'",
                        new { s.PortfolioId, s.LastChecked });
                    if (failed is not null)
                        items.Add(new AlertItem(AlertKinds.Sync, $"The Navexa sync didn't finish: {failed["Sync failed:".Length..].Trim()}"));
                }
                catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { }
            }
            if (s.Unallocated && (s.UnallocatedSent is null || s.UnallocatedSent.Value.Date < utcNow.Date))
            {
                var waiting = (await con.QueryAsync("uspGetUnallocatedTransactions", new { iUserId = s.PortfolioId }, commandType: CommandType.StoredProcedure)).Count();
                if (waiting > 0)
                    items.Add(new AlertItem(AlertKinds.Unallocated, $"{waiting} imported bank line{(waiting == 1 ? " is" : "s are")} waiting to be allocated."));
            }
            if (s.Property)
                items.AddRange(await PropertyItemsAsync(s.PortfolioId, DateOnly.FromDateTime(utcNow)));
            digests.Add(new AlertDigest(s.LoginId, s.PortfolioId, s.Email.Trim(), s.PortfolioName.Trim(), items));
        }
        return digests;
    }

    /// <summary>Notes what was sent, so nothing is sent twice. Called for every subscriber checked, with or without news.</summary>
    public async Task MarkCheckedAsync(AlertDigest d, DateTime utcNow)
    {
        await using var con = db.Create();
        foreach (var i in d.Items.Where(i => i.Key is not null))
            await properties.MarkAlertSentAsync(d.PortfolioId, i.Key!);
        foreach (var i in d.Items.Where(i => i.PriceAlertId is not null))
            await con.ExecuteAsync("uspMarkPriceAlertSent", new { iPriceAlertID = i.PriceAlertId, dblPrice = (double)(i.Price ?? 0) },
                commandType: CommandType.StoredProcedure);
        await con.ExecuteAsync("uspMarkAlertsChecked", new { iLoginID = d.LoginId, iPortfolioID = d.PortfolioId, dtmChecked = utcNow, bUnallocatedSent = d.HasUnallocated },
            commandType: CommandType.StoredProcedure);
    }
}
