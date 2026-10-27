using System.Data;
using System.Data.Common;
using System.Text.Json;
using Dapper;
using PortfolioManager.Core.Crypto;
using PortfolioManager.Core.Data;
using PortfolioManager.Core.Import;

namespace PortfolioManager.Core.NavexaSync;

/// <summary>Encrypts the Navexa API key before it is stored. The web app supplies one built on ASP.NET Data Protection.</summary>
public interface INavexaKeyProtector
{
    string Protect(string apiKey);

    /// <summary>Null when the key can't be read back (e.g. the server's encryption keys were lost).</summary>
    string? Unprotect(string protectedKey);
}

public enum SyncSchedule { Off, Daily, Weekly }

public sealed record NavexaConnection(
    int PortfolioId,
    string ProtectedKey,
    string? KeyHint,
    int NavexaPortfolioId,
    string? NavexaPortfolioName,
    string? TradeBankAccount,
    DateTime? ImportFrom,
    SyncSchedule Schedule,
    DateTime? LastCheckedUtc,
    string? LastResult);

public sealed record NavexaMapping(string Kind, string Key, string? Label, bool Skip, string? Exchange, string? Code, int? AccountId)
{
    public const string Holding = "Holding";
    public const string CashAccount = "CashAccount";
    public const string CashType = "CashType";
}

public sealed record SyncRunResult(int Added, int Failed, SyncPlan Plan, IReadOnlyList<string> Errors)
{
    public string Summary
    {
        get
        {
            var parts = new List<string> { $"{Added:N0} added" };
            if (Failed > 0) parts.Add($"{Failed:N0} failed");
            if (Plan.Count(SyncStatus.NeedsInput) is > 0 and var n) parts.Add($"{n:N0} need your input");
            if (Plan.Count(SyncStatus.Waiting) is > 0 and var w) parts.Add($"{w:N0} waiting for Navexa");
            return string.Join(", ", parts) + ".";
        }
    }
}

/// <summary>
/// Copies a Navexa portfolio into a portfolio here through the Navexa API: once, or again whenever it is run
/// (by Sync now or on a schedule). Every Navexa record is remembered in tblNavexaSyncItem, so running it again
/// only adds what is new. Trades, payouts, DRPs, splits, returns of capital and cash all go through the usual
/// procs, which post the double-entry journals.
/// </summary>
public sealed class NavexaSyncService(IDbConnectionFactory db, NavexaApiClient api, INavexaKeyProtector protector)
{
    // ---------- Connection and settings ----------

    public async Task<NavexaConnection?> GetConnectionAsync(int portfolioId)
    {
        await using var con = db.Create();
        var row = await con.QuerySingleOrDefaultAsync(
            "SELECT * FROM tblNavexaConnection WHERE iUserId = @portfolioId", new { portfolioId });
        return row is null ? null : MapConnection((IDictionary<string, object?>)row);
    }

    private static NavexaConnection MapConnection(IDictionary<string, object?> r) => new(
        (int)r["iUserId"]!,
        (string)r["strApiKeyProtected"]!,
        r["strApiKeyHint"] as string,
        (int)r["iNavexaPortfolioId"]!,
        r["strNavexaPortfolioName"] as string,
        r["strTradeBankAccount"] as string,
        r["dtmImportFrom"] as DateTime?,
        Enum.TryParse<SyncSchedule>(r["strSchedule"] as string, true, out var s) ? s : SyncSchedule.Off,
        r["dtmLastChecked"] as DateTime?,
        r["strLastResult"] as string);

    /// <summary>The Navexa portfolios a key can see, to choose from. Nothing is saved.</summary>
    public Task<IReadOnlyList<NavexaApiPortfolio>> GetNavexaPortfoliosAsync(string apiKey, CancellationToken ct = default) =>
        api.GetPortfoliosAsync(apiKey, ct);

    public string ProtectKey(string apiKey) => protector.Protect(apiKey.Trim());

    public string? UnprotectKey(string protectedKey) => protector.Unprotect(protectedKey);

    /// <summary>Saves (or replaces) the key and the Navexa portfolio. The key is stored encrypted.</summary>
    public async Task ConnectAsync(int portfolioId, string apiKey, int navexaPortfolioId, string navexaPortfolioName, int loginId)
    {
        var key = apiKey.Trim();
        await using var con = db.Create();
        await con.ExecuteAsync("""
            IF EXISTS (SELECT 1 FROM tblNavexaConnection WHERE iUserId = @portfolioId)
                UPDATE tblNavexaConnection
                SET strApiKeyProtected = @protectedKey, strApiKeyHint = @hint, iNavexaPortfolioId = @navexaPortfolioId,
                    strNavexaPortfolioName = @navexaPortfolioName, iConnectedByLoginId = @loginId
                WHERE iUserId = @portfolioId
            ELSE
                INSERT INTO tblNavexaConnection (iUserId, strApiKeyProtected, strApiKeyHint, iNavexaPortfolioId, strNavexaPortfolioName, iConnectedByLoginId)
                VALUES (@portfolioId, @protectedKey, @hint, @navexaPortfolioId, @navexaPortfolioName, @loginId)
            """, new
        {
            portfolioId,
            protectedKey = protector.Protect(key),
            hint = KeyHint(key),
            navexaPortfolioId,
            navexaPortfolioName = Truncate(navexaPortfolioName, 200),
            loginId,
        });
    }

    /// <summary>Points the saved connection at another Navexa portfolio, keeping the key. Its id is Navexa's own, not this site's.</summary>
    public async Task SetNavexaPortfolioAsync(int portfolioId, int navexaPortfolioId, string navexaPortfolioName)
    {
        await using var con = db.Create();
        await con.ExecuteAsync("""
            UPDATE tblNavexaConnection SET iNavexaPortfolioId = @navexaPortfolioId, strNavexaPortfolioName = @navexaPortfolioName
            WHERE iUserId = @portfolioId
            """, new { portfolioId, navexaPortfolioId, navexaPortfolioName = Truncate(navexaPortfolioName, 200) });
    }

    /// <summary>
    /// The name Navexa gives a portfolio id typed in by hand. When the key can't list portfolios, or the id isn't in the
    /// list (a group or a portfolio shared another way), the id is still used, under a plain name. A refused key still fails.
    /// </summary>
    public async Task<string> NavexaPortfolioNameAsync(string apiKey, int navexaPortfolioId, CancellationToken ct = default)
    {
        try
        {
            var portfolios = await api.GetPortfoliosAsync(apiKey, ct);
            if (portfolios.FirstOrDefault(p => p.Id == navexaPortfolioId) is { } found)
                return found.Name;
        }
        catch (NavexaApiException ex) when (ex.Status is null or System.Net.HttpStatusCode.NotFound)
        {
        }
        return $"Navexa portfolio {navexaPortfolioId}";
    }

    public static string KeyHint(string key) => key.Length <= 4 ? "" : key[^4..];

    public async Task SaveSettingsAsync(int portfolioId, string? tradeBankAccount, DateOnly? importFrom, SyncSchedule schedule)
    {
        await using var con = db.Create();
        await con.ExecuteAsync("""
            UPDATE tblNavexaConnection
            SET strTradeBankAccount = @tradeBankAccount, dtmImportFrom = @importFrom, strSchedule = @schedule
            WHERE iUserId = @portfolioId
            """, new
        {
            portfolioId,
            tradeBankAccount = string.IsNullOrWhiteSpace(tradeBankAccount) ? null : Truncate(tradeBankAccount.Trim(), 500),
            importFrom = importFrom?.ToDateTime(TimeOnly.MinValue),
            schedule = schedule.ToString(),
        });
    }

    /// <summary>Removes the saved key. What was added stays, and so does the record of it, so reconnecting doesn't add it twice.</summary>
    public async Task DisconnectAsync(int portfolioId)
    {
        await using var con = db.Create();
        await con.ExecuteAsync("DELETE FROM tblNavexaConnection WHERE iUserId = @portfolioId", new { portfolioId });
    }

    public async Task<IReadOnlyList<NavexaMapping>> GetMappingsAsync(int portfolioId)
    {
        await using var con = db.Create();
        var rows = await con.QueryAsync<NavexaMapping>("""
            SELECT strKind AS Kind, strKey AS [Key], strLabel AS Label, bSkip AS Skip, strExchange AS Exchange, strCode AS Code, iAccountID AS AccountId
            FROM tblNavexaMapping WHERE iUserId = @portfolioId ORDER BY strKind, strLabel
            """, new { portfolioId });
        return rows.ToList();
    }

    public async Task SaveMappingsAsync(int portfolioId, IEnumerable<NavexaMapping> mappings)
    {
        await using var con = db.Create();
        await con.OpenAsync();
        await using var tx = await con.BeginTransactionAsync();
        foreach (var m in mappings)
        {
            await con.ExecuteAsync("""
                IF EXISTS (SELECT 1 FROM tblNavexaMapping WHERE iUserId = @portfolioId AND strKind = @Kind AND strKey = @Key)
                    UPDATE tblNavexaMapping SET strLabel = @Label, bSkip = @Skip, strExchange = @Exchange, strCode = @Code, iAccountID = @AccountId
                    WHERE iUserId = @portfolioId AND strKind = @Kind AND strKey = @Key
                ELSE
                    INSERT INTO tblNavexaMapping (iUserId, strKind, strKey, strLabel, bSkip, strExchange, strCode, iAccountID)
                    VALUES (@portfolioId, @Kind, @Key, @Label, @Skip, @Exchange, @Code, @AccountId)
                """, new { portfolioId, m.Kind, m.Key, Label = Truncate(m.Label, 500), m.Skip, m.Exchange, m.Code, m.AccountId }, tx);
        }
        await tx.CommitAsync();
    }

    /// <summary>The planner's view of the saved mappings.</summary>
    public static SyncSettings Settings(string portfolioCurrency, NavexaConnection c, IReadOnlyList<NavexaMapping> mappings) => new(
        portfolioCurrency,
        c.TradeBankAccount,
        c.ImportFrom,
        mappings.Where(m => m.Kind == NavexaMapping.Holding && (m.Skip || (!string.IsNullOrWhiteSpace(m.Exchange) && !string.IsNullOrWhiteSpace(m.Code))))
            .ToDictionary(m => m.Key, m => m.Skip ? null : new HoldingMapping(m.Exchange!.Trim().ToUpperInvariant(), m.Code!.Trim().ToUpperInvariant())),
        mappings.Where(m => m.Kind == NavexaMapping.CashAccount && (m.Skip || !string.IsNullOrWhiteSpace(m.Code)))
            .ToDictionary(m => m.Key, m => m.Skip ? null : m.Code!.Trim()),
        // A saved cash type with no ledger account means Owner's Capital, as for a deposit entered by hand
        mappings.Where(m => m.Kind == NavexaMapping.CashType).ToDictionary(m => m.Key, m => m.AccountId));

    // ---------- Checking Navexa and reviewing ----------

    /// <summary>Reads everything from Navexa, works out what is new and what needs input, and remembers it. Adds nothing.</summary>
    public async Task<SyncPlan> CheckAsync(int portfolioId, CancellationToken ct = default)
    {
        var connection = await GetConnectionAsync(portfolioId) ?? throw new NavexaApiException("Connect to Navexa first.");
        var key = protector.Unprotect(connection.ProtectedKey)
            ?? throw new NavexaApiException("The saved Navexa API key can't be read any more. Enter it again.");
        var transactions = await api.GetTransactionsAsync(key, connection.NavexaPortfolioId, DateOnly.FromDateTime(DateTime.Today.AddDays(1)), ct);

        var (plan, _) = await PlanAsync(portfolioId, connection, transactions);
        await StoreAsync(portfolioId, plan, transactions);
        await SetLastResultAsync(portfolioId, $"Checked: {plan.Count(SyncStatus.Ready):N0} ready to add, " +
            $"{plan.Count(SyncStatus.NeedsInput):N0} need your input.", checkedNow: true);
        return plan;
    }

    /// <summary>The last check, worked out again from what was saved with today's settings and answers. No call to Navexa.</summary>
    public async Task<SyncPlan?> GetSavedPlanAsync(int portfolioId)
    {
        var connection = await GetConnectionAsync(portfolioId);
        if (connection is null)
            return null;
        var saved = await LoadPayloadsAsync(portfolioId);
        var (plan, _) = await PlanAsync(portfolioId, connection, saved);
        return plan;
    }

    /// <summary>Saves the answers typed in on the review page (franking credits, DRP units, split ratios, duplicates).</summary>
    public async Task SaveInputsAsync(int portfolioId, IReadOnlyDictionary<string, SyncInput> inputs)
    {
        await using var con = db.Create();
        foreach (var (key, input) in inputs)
        {
            var (type, id) = SplitKey(key);
            await con.ExecuteAsync("""
                UPDATE tblNavexaSyncItem SET strInput = @input, dtmUpdated = SYSUTCDATETIME()
                WHERE iUserId = @portfolioId AND strNavexaType = @type AND strNavexaId = @id AND strStatus <> 'Imported'
                """, new { portfolioId, type, id, input = JsonSerializer.Serialize(input, NavexaApiClient.Json) });
        }
    }

    // ---------- Adding ----------

    /// <summary>Adds every ready record from the last check, oldest first, each in its own database transaction.</summary>
    public async Task<SyncRunResult> AddReadyAsync(int portfolioId, CancellationToken ct = default)
    {
        var connection = await GetConnectionAsync(portfolioId) ?? throw new NavexaApiException("Connect to Navexa first.");
        var saved = await LoadPayloadsAsync(portfolioId);
        var (plan, _) = await PlanAsync(portfolioId, connection, saved);
        await StoreAsync(portfolioId, plan, saved);
        var result = await PostAsync(portfolioId, plan, ct);
        await SetLastResultAsync(portfolioId, result.Summary, checkedNow: false);
        return result;
    }

    /// <summary>Sync now, or a scheduled run: check Navexa, then add everything that is ready. Items needing input wait.</summary>
    public async Task<SyncRunResult> SyncAsync(int portfolioId, CancellationToken ct = default)
    {
        var connection = await GetConnectionAsync(portfolioId) ?? throw new NavexaApiException("Connect to Navexa first.");
        var key = protector.Unprotect(connection.ProtectedKey)
            ?? throw new NavexaApiException("The saved Navexa API key can't be read any more. Enter it again.");
        var transactions = await api.GetTransactionsAsync(key, connection.NavexaPortfolioId, DateOnly.FromDateTime(DateTime.Today.AddDays(1)), ct);
        var (plan, _) = await PlanAsync(portfolioId, connection, transactions);
        await StoreAsync(portfolioId, plan, transactions);
        var result = await PostAsync(portfolioId, plan, ct);
        await SetLastResultAsync(portfolioId, result.Summary, checkedNow: true);
        return result;
    }

    /// <summary>Portfolios whose schedule says a sync is due. A portfolio is only synced on a schedule after its first check.</summary>
    public async Task<IReadOnlyList<int>> GetDueAsync(DateTime utcNow)
    {
        await using var con = db.Create();
        var rows = await con.QueryAsync<int>("""
            SELECT iUserId FROM tblNavexaConnection
            WHERE dtmLastChecked IS NOT NULL
              AND ((strSchedule = 'Daily' AND dtmLastChecked <= @daily) OR (strSchedule = 'Weekly' AND dtmLastChecked <= @weekly))
            """, new { daily = utcNow.AddHours(-20), weekly = utcNow.AddDays(-7).AddHours(4) });
        return rows.ToList();
    }

    public async Task RecordFailureAsync(int portfolioId, string message) =>
        await SetLastResultAsync(portfolioId, $"Sync failed: {message}", checkedNow: true);

    private async Task<SyncRunResult> PostAsync(int portfolioId, SyncPlan plan, CancellationToken ct)
    {
        var ready = plan.Ready.OrderBy(i => i.Date).ThenBy(i => i.PostingOrder).ThenBy(i => i.NavexaId, StringComparer.Ordinal).ToList();
        int added = 0, failed = 0;
        var errors = new List<string>();

        await using var con = db.Create();
        await con.OpenAsync(ct);
        foreach (var item in ready)
        {
            ct.ThrowIfCancellationRequested();
            await using var tx = await con.BeginTransactionAsync(ct);
            try
            {
                // Claim the record first: a second run at the same time finds it already Imported and leaves it alone
                var claimed = await con.ExecuteAsync("""
                    UPDATE tblNavexaSyncItem
                    SET strStatus = 'Imported', strMessage = NULL, strPayload = NULL, dtmImported = SYSUTCDATETIME(),
                        dtmUpdated = SYSUTCDATETIME(), dtmNavexaModified = @modified
                    WHERE iUserId = @portfolioId AND strNavexaType = @type AND strNavexaId = @id AND strStatus <> 'Imported'
                    """, new { portfolioId, type = item.NavexaType, id = item.NavexaId, modified = item.Modified }, tx);
                if (claimed == 0)
                {
                    await tx.RollbackAsync(ct);
                    continue;
                }
                await PostItemAsync(con, tx, portfolioId, item);
                await tx.CommitAsync(ct);
                item.Status = SyncStatus.Imported;
                added++;
            }
            catch (Exception ex) when (ex is DbException or SyncPostException)
            {
                try
                {
                    await tx.RollbackAsync(CancellationToken.None);
                }
                catch (InvalidOperationException)
                {
                    // SQL Server already rolled it back (XACT_ABORT)
                }
                failed++;
                item.Status = SyncStatus.Failed;
                item.Message = Truncate(ex.Message, 1000);
                errors.Add($"{item.Date:d} {item.Description}: {item.Message}");
                await con.ExecuteAsync("""
                    UPDATE tblNavexaSyncItem SET strStatus = 'Failed', strMessage = @message, dtmUpdated = SYSUTCDATETIME()
                    WHERE iUserId = @portfolioId AND strNavexaType = @type AND strNavexaId = @id AND strStatus <> 'Imported'
                    """, new { portfolioId, type = item.NavexaType, id = item.NavexaId, message = item.Message });
            }
        }
        return new SyncRunResult(added, failed, plan, errors);
    }

    private sealed class SyncPostException(string message) : Exception(message);

    /// <summary>Posts one record through its proc. Each proc posts the double-entry journal for Hybrid/DoubleEntry portfolios.</summary>
    private static async Task PostItemAsync(DbConnection con, DbTransaction tx, int portfolioId, PlannedItem item)
    {
        switch (item.Kind)
        {
            case SyncKind.Trade when item.Trade is { } t:
                await con.ExecuteAsync(t.BankAccount is null ? "uspAddShareTrade" : "AddShareTradeWithCashTransaction",
                    new DynamicParameters(NavexaSyncParameters.Trade(portfolioId, t)), tx, commandType: CommandType.StoredProcedure);
                break;
            case SyncKind.Payout when item.Payout is { } p:
                await con.ExecuteAsync("uspAddPayout", new DynamicParameters(NavexaSyncParameters.Payout(portfolioId, p)), tx,
                    commandType: CommandType.StoredProcedure);
                break;
            case SyncKind.Drp when item.Drp is { } d:
                await RunResultProcAsync(con, tx, "uspAddDRPTransaction", NavexaSyncParameters.Drp(portfolioId, d), "DRP Added");
                break;
            case SyncKind.Cash when item.Cash is { } c:
                await RunResultProcAsync(con, tx, "uspAddNavexaCashTransaction", NavexaSyncParameters.Cash(portfolioId, c), "Cash Transaction Added");
                break;
            case SyncKind.Split when item.Split is { } s:
                await RunResultProcAsync(con, tx, "uspApplyStockSplit", NavexaSyncParameters.Split(portfolioId, s), "Split Applied");
                break;
            case SyncKind.CapitalReturn when item.CapitalReturn is { } r:
                await RunResultProcAsync(con, tx, "uspAddReturnOfCapital", NavexaSyncParameters.CapitalReturn(portfolioId, r), "Return Of Capital Added");
                break;
            case SyncKind.Swap when item.Swap is { } swap:
                await CryptoService.PostSwapAsync(con, tx, portfolioId, swap);
                break;
            case SyncKind.Reward when item.Reward is { } reward:
                if (await CryptoService.PostRewardAsync(con, tx, portfolioId, reward) is { Ok: false } failed)
                    throw new SyncPostException(failed.Error!);
                break;
            default:
                throw new SyncPostException("Nothing to add for this record.");
        }
    }

    private static async Task RunResultProcAsync(DbConnection con, DbTransaction tx, string proc, Dictionary<string, object?> parameters, string success)
    {
        var row = (IDictionary<string, object?>?)await con.QueryFirstOrDefaultAsync(proc, new DynamicParameters(parameters), tx,
            commandType: CommandType.StoredProcedure);
        var text = row is not null && row.TryGetValue("Result", out var v) && v is not null and not DBNull ? Convert.ToString(v) : null;
        var result = ProcResult.From(text, success);
        if (!result.Ok)
            throw new SyncPostException(result.Error!);
    }

    // ---------- Storage ----------

    private async Task<(SyncPlan Plan, string Currency)> PlanAsync(int portfolioId, NavexaConnection connection,
        IReadOnlyList<NavexaApiTransaction> transactions)
    {
        await using var con = db.Create();
        var currency = (await con.ExecuteScalarAsync<string?>(
            "SELECT strPortfolioCurrency FROM tblUsers WHERE iUserId = @portfolioId", new { portfolioId }))?.Trim();
        currency = string.IsNullOrEmpty(currency) ? "AUD" : currency.ToUpperInvariant();

        var mappings = await GetMappingsAsync(portfolioId);
        var known = (await con.QueryAsync<(string Type, string Id, string Status, DateTime? Modified, string? Input)>("""
            SELECT strNavexaType, strNavexaId, strStatus, dtmNavexaModified, strInput
            FROM tblNavexaSyncItem WHERE iUserId = @portfolioId
            """, new { portfolioId }))
            .ToDictionary(k => $"{k.Type}:{k.Id}", k => new KnownItem(
                Enum.TryParse<SyncStatus>(k.Status, out var s) ? s : SyncStatus.Ready,
                k.Modified,
                k.Input is { Length: > 0 } json ? JsonSerializer.Deserialize<SyncInput>(json, NavexaApiClient.Json) : null));

        var existing = new ExistingRecords(
            (await con.QueryAsync<ExistingTrade>("""
                SELECT ISNULL(strStockExchangeCode, '') AS Exchange, ISNULL(strStockCode, '') AS Code, ISNULL(strType, '') AS Type,
                       CAST(dtmDateOfTransaction AS datetime) AS Date, CAST(ROUND(ISNULL(iQuantity, 0), 8) AS decimal(28, 8)) AS Quantity,
                       CAST(ISNULL(dblPricePerUnit, 0) AS decimal(19, 6)) AS Price
                FROM tblShareTrades WHERE iUserId = @portfolioId
                """, new { portfolioId })).ToList(),
            (await con.QueryAsync<ExistingPayout>("""
                SELECT ISNULL(strStockExchangeCode, '') AS Exchange, ISNULL(strStockCode, '') AS Code,
                       CAST(dtmTransactionDate AS datetime) AS Date, CAST(ISNULL(dblTotal, 0) AS decimal(19, 4)) AS Total
                FROM tblPayouts WHERE iUserID = @portfolioId AND dtmTransactionDate IS NOT NULL
                """, new { portfolioId })).ToList(),
            (await con.QueryAsync<ExistingCash>("""
                SELECT ISNULL(strBankAccountName, '') AS Account, CAST(dtmDateOfTransaction AS datetime) AS Date,
                       CAST(ISNULL(dblDepositAmount, 0) AS decimal(19, 2)) AS Deposit, CAST(ISNULL(dblWithdrawalAmount, 0) AS decimal(19, 2)) AS Withdrawal
                FROM tblBankStatement WHERE iUserId = @portfolioId AND iShareTradeId IS NULL
                """, new { portfolioId })).ToList());

        var plan = NavexaSyncPlanner.Plan(transactions, Settings(currency, connection, mappings), known, existing);
        return (plan, currency);
    }

    /// <summary>
    /// Remembers every record and its status. Records already imported keep that status (only the "changed in Navexa" flag moves);
    /// the rest keep Navexa's JSON so they can be added later without asking Navexa again.
    /// </summary>
    private async Task StoreAsync(int portfolioId, SyncPlan plan, IReadOnlyList<NavexaApiTransaction> transactions)
    {
        var payloads = transactions.GroupBy(NavexaSyncPlanner.Key).ToDictionary(g => g.Key, g => g.First());
        await using var con = db.Create();
        await con.OpenAsync();
        await using var tx = await con.BeginTransactionAsync();
        foreach (var item in plan.Items)
        {
            var payload = item.Status == SyncStatus.Imported || !payloads.TryGetValue(item.Key, out var t)
                ? null
                : JsonSerializer.Serialize(t, NavexaApiClient.Json);
            await con.ExecuteAsync("""
                IF EXISTS (SELECT 1 FROM tblNavexaSyncItem WHERE iUserId = @portfolioId AND strNavexaType = @type AND strNavexaId = @id)
                    UPDATE tblNavexaSyncItem
                    SET strStatus = CASE WHEN strStatus = 'Imported' THEN 'Imported' ELSE @status END,
                        strKind = @kind, strDescription = @description, dtmDate = @date,
                        strMessage = CASE WHEN strStatus = 'Imported' AND @status <> 'Imported' THEN strMessage ELSE @message END,
                        strPayload = CASE WHEN strStatus = 'Imported' OR @status = 'Imported' THEN NULL ELSE @payload END,
                        dtmNavexaModified = CASE WHEN strStatus = 'Imported' THEN dtmNavexaModified ELSE @modified END,
                        bChangedInNavexa = @changed,
                        dtmImported = CASE WHEN strStatus <> 'Imported' AND @status = 'Imported' THEN SYSUTCDATETIME() ELSE dtmImported END,
                        dtmUpdated = SYSUTCDATETIME()
                    WHERE iUserId = @portfolioId AND strNavexaType = @type AND strNavexaId = @id
                ELSE
                    INSERT INTO tblNavexaSyncItem (iUserId, strNavexaType, strNavexaId, dtmDate, strStatus, strKind, strDescription, strMessage,
                        strPayload, dtmNavexaModified, bChangedInNavexa, dtmImported)
                    VALUES (@portfolioId, @type, @id, @date, @status, @kind, @description, @message, @payload, @modified, @changed,
                        CASE WHEN @status = 'Imported' THEN SYSUTCDATETIME() END)
                """, new
            {
                portfolioId,
                type = item.NavexaType,
                id = item.NavexaId,
                date = item.Date,
                status = item.Status.ToString(),
                kind = item.Kind == SyncKind.None ? null : item.Kind.ToString(),
                description = Truncate(item.Description, 300),
                message = item.Message is null ? null : Truncate(item.Message, 1000),
                payload,
                modified = item.Modified,
                changed = item.ChangedInNavexa,
            }, tx);
        }

        // Records deleted in Navexa before they were added here are forgotten; added ones are kept for good
        var keep = plan.Items.Select(i => i.Key).ToHashSet();
        var stale = (await con.QueryAsync<(string Type, string Id)>(
            "SELECT strNavexaType, strNavexaId FROM tblNavexaSyncItem WHERE iUserId = @portfolioId AND strStatus <> 'Imported'",
            new { portfolioId }, tx)).Where(k => !keep.Contains($"{k.Type}:{k.Id}")).ToList();
        foreach (var (type, id) in stale)
            await con.ExecuteAsync(
                "DELETE FROM tblNavexaSyncItem WHERE iUserId = @portfolioId AND strNavexaType = @type AND strNavexaId = @id AND strStatus <> 'Imported'",
                new { portfolioId, type, id }, tx);
        await tx.CommitAsync();
    }

    private async Task<IReadOnlyList<NavexaApiTransaction>> LoadPayloadsAsync(int portfolioId)
    {
        await using var con = db.Create();
        var rows = await con.QueryAsync<string>(
            "SELECT strPayload FROM tblNavexaSyncItem WHERE iUserId = @portfolioId AND strStatus <> 'Imported' AND strPayload IS NOT NULL",
            new { portfolioId });
        return rows.Select(json => JsonSerializer.Deserialize<NavexaApiTransaction>(json, NavexaApiClient.Json))
            .Where(t => t is not null).Select(t => t!).ToList();
    }

    /// <summary>How many records have been added from Navexa so far.</summary>
    public async Task<int> CountImportedAsync(int portfolioId)
    {
        await using var con = db.Create();
        return await con.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM tblNavexaSyncItem WHERE iUserId = @portfolioId AND strStatus = 'Imported'", new { portfolioId });
    }

    private async Task SetLastResultAsync(int portfolioId, string result, bool checkedNow)
    {
        await using var con = db.Create();
        await con.ExecuteAsync("""
            UPDATE tblNavexaConnection
            SET strLastResult = @result, dtmLastChecked = CASE WHEN @checkedNow = 1 THEN SYSUTCDATETIME() ELSE dtmLastChecked END
            WHERE iUserId = @portfolioId
            """, new { portfolioId, result = Truncate(result, 1000), checkedNow });
    }

    private static (string Type, string Id) SplitKey(string key)
    {
        var colon = key.IndexOf(':');
        return colon < 0 ? (key, "") : (key[..colon], key[(colon + 1)..]);
    }

    private static string? Truncate(string? s, int max) => s is null || s.Length <= max ? s : s[..max];
}
