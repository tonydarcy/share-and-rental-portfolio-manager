using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using Dapper;
using PortfolioManager.Core.Data;

namespace PortfolioManager.Core.Backup;

/// <summary>What a restore put in, and anything it couldn't.</summary>
public sealed record RestoreResult(int PortfolioId, string Name, IReadOnlyDictionary<string, int> Rows, IReadOnlyList<string> Notes)
{
    public int Total => Rows.Values.Sum();
    public int Of(string table) => Rows.TryGetValue(table, out var n) ? n : 0;
}

/// <summary>
/// Backs a portfolio up to a file and restores one into a new portfolio, on this copy of the site or another.
/// The backup copies the rows the stored procs already wrote (journals, parcel matching and all), so the books come
/// back exactly as they were; replaying them through the procs would lose bank matches, reconciliations and edits.
/// Every id is renumbered on the way in and only ever points at rows from the same file. Columns are matched by
/// name, so a backup from an older or newer copy of the site still restores: anything this database hasn't got
/// is listed in the notes, and anything the file hasn't got gets the column's default.
/// </summary>
public sealed class BackupService(IDbConnectionFactory db)
{
    private const int LongTimeout = 600;
    private const int MaxParameters = 2000;

    public sealed record Column(string Table, string Name, string Type, bool Nullable, bool IsIdentity, bool HasDefault, bool IsComputed);

    private async Task<Dictionary<string, List<Column>>> SchemaAsync(DbConnection con, DbTransaction? tx = null)
    {
        var rows = await con.QueryAsync<Column>("""
            SELECT t.name AS [Table], c.name AS Name, ty.name AS Type, c.is_nullable AS Nullable, c.is_identity AS IsIdentity,
                   CAST(CASE WHEN c.default_object_id <> 0 THEN 1 ELSE 0 END AS bit) AS HasDefault, c.is_computed AS IsComputed
            FROM sys.tables t JOIN sys.columns c ON c.object_id = t.object_id JOIN sys.types ty ON ty.user_type_id = c.user_type_id
            ORDER BY t.name, c.column_id
            """, transaction: tx);
        return rows.GroupBy(r => r.Table, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Whether the table and every parent it is found through exist here (an older database may lack some).</summary>
    private static bool Reachable(BackupTable table, Dictionary<string, List<Column>> schema) =>
        schema.ContainsKey(table.Name)
        && (table.Parent is null || (BackupPlan.Find(table.Parent) is { } parent && Reachable(parent, schema)));

    /// <summary>Reads everything in the portfolio into a backup.</summary>
    public async Task<BackupFile> CreateAsync(int portfolioId, bool withAttachments, string? source, CancellationToken cancel = default)
    {
        await using var con = db.Create();
        await con.OpenAsync(cancel);
        var schema = await SchemaAsync(con);

        var users = schema["tblUsers"];
        var settingColumns = BackupPlan.SettingColumns.Where(s => users.Any(c => c.Name.Equals(s, StringComparison.OrdinalIgnoreCase))).ToList();
        var select = string.Join(", ", settingColumns.Select(c => $"[{c}]").Prepend("[strEntityName]"));
        BackupFile file;
        await using (var reader = await con.ExecuteReaderAsync(new CommandDefinition(
            $"SELECT {select} FROM tblUsers WHERE iUserId = @p", new { p = portfolioId }, cancellationToken: cancel)))
        {
            if (!await reader.ReadAsync(cancel))
                throw new InvalidOperationException($"Portfolio {portfolioId} not found.");
            file = new BackupFile { Source = source, PortfolioName = reader.GetString(0).Trim() };
            for (var i = 1; i < reader.FieldCount; i++)
                file.Settings[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
        }

        foreach (var table in BackupPlan.Tables)
        {
            if (!Reachable(table, schema) || (!withAttachments && table.Name == "tblAttachments"))
                continue;
            var columns = schema[table.Name].Where(c => !c.IsComputed).Select(c => c.Name).ToList();
            var sql = $"SELECT {string.Join(", ", columns.Select(c => $"x.[{c}]"))} FROM [{table.Name}] x WHERE {BackupPlan.RowFilter(table, "x")}";
            var data = new BackupTableData(table.Name, columns);
            await using var reader = await con.ExecuteReaderAsync(new CommandDefinition(sql, new { p = portfolioId },
                commandTimeout: LongTimeout, cancellationToken: cancel));
            while (await reader.ReadAsync(cancel))
            {
                var row = new object?[columns.Count];
                for (var i = 0; i < row.Length; i++)
                    row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                data.Rows.Add(row);
            }
            file.Tables.Add(data);
        }
        return file;
    }

    /// <summary>One column a restore fills, and where its value comes from.</summary>
    private enum Role { Plain, Owner, Login, Id, Later, Source, LineXml }

    private sealed record Target(Column Column, int FileIndex, Role Role, IdColumn? Id = null, SourceColumn? Source = null, int KindIndex = -1);

    private sealed record Pending(string Table, string IdColumn, long OldRowId, string Column, string? RefTable, long OldValue, bool Nullable);

    /// <summary>
    /// Restores a backup as a new portfolio owned by the login, in one transaction: if anything fails, nothing is
    /// left behind. Existing portfolios are never touched.
    /// </summary>
    public async Task<RestoreResult> RestoreAsync(int loginId, BackupFile file, string name, CancellationToken cancel = default)
    {
        await using var con = db.Create();
        await con.OpenAsync(cancel);
        await using var tx = await con.BeginTransactionAsync(cancel);
        var schema = await SchemaAsync(con, tx);
        var notes = new List<string>();
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // 1. The portfolio itself, through the same proc as Manage portfolios, then its settings
        var entityType = BackupValues.ToText(file.Settings.GetValueOrDefault("strEntityType"))?.Trim();
        await con.ExecuteAsync("AddNewPortfolio", new { iUserID = loginId, strEntityName = name.Trim(), strEntityType = entityType ?? "Individual" },
            tx, commandType: CommandType.StoredProcedure);
        var portfolioId = await con.ExecuteScalarAsync<int>("""
            SELECT TOP 1 iUserIDForPortfolioToAccess FROM tblPortfolioPermissions
            WHERE iUserID = @loginId ORDER BY iPortfolioPermissionsID DESC
            """, new { loginId }, tx);
        await RestoreSettingsAsync(con, tx, schema["tblUsers"], file, portfolioId);

        // 2. Every table, in the plan's order so the rows each one points at are already in
        var map = new IdMap();
        var pending = new List<Pending>();
        foreach (var table in BackupPlan.Tables.Where(t => !t.Last))
            await RestoreTableAsync(con, tx, table, file, schema, map, pending, portfolioId, loginId, counts, notes, cancel);

        // 3. Ids that point forward or at their own table, now that every row has its new id
        await FixPendingAsync(con, tx, pending, map, cancel);

        // 4. Locked periods last, so the lock triggers don't refuse the rows dated before them
        foreach (var table in BackupPlan.Tables.Where(t => t.Last))
            await RestoreTableAsync(con, tx, table, file, schema, map, pending, portfolioId, loginId, counts, notes, cancel);

        foreach (var data in file.Tables.Where(t => BackupPlan.Find(t.Name) is null && t.Rows.Count > 0))
            notes.Add($"{Describe(data.Name)}: {data.Rows.Count} record(s) not restored, as this copy of the site doesn't know them. " +
                      "Update the site and run its latest database migrations, then restore again.");

        await tx.CommitAsync(cancel);
        return new RestoreResult(portfolioId, name.Trim(), counts, notes);
    }

    private static async Task RestoreSettingsAsync(DbConnection con, DbTransaction tx, List<Column> users, BackupFile file, int portfolioId)
    {
        var parameters = new DynamicParameters();
        parameters.Add("p", portfolioId);
        var sets = new List<string>();
        foreach (var setting in BackupPlan.SettingColumns)
        {
            var column = users.FirstOrDefault(c => c.Name.Equals(setting, StringComparison.OrdinalIgnoreCase));
            if (column is null || !file.Settings.TryGetValue(setting, out var value))
                continue;
            var converted = BackupValues.Convert(value, column.Type);
            if (converted is null && !column.Nullable)
                continue;
            parameters.Add("s" + sets.Count, converted, BackupValues.DbTypeFor(column.Type));
            sets.Add($"[{column.Name}] = @s{sets.Count}");
        }
        if (sets.Count > 0)
            await con.ExecuteAsync($"UPDATE tblUsers SET {string.Join(", ", sets)} WHERE iUserId = @p", parameters, tx);
    }

    private static async Task RestoreTableAsync(DbConnection con, DbTransaction tx, BackupTable table, BackupFile file,
        Dictionary<string, List<Column>> schema, IdMap map, List<Pending> pending, int portfolioId, int loginId,
        Dictionary<string, int> counts, List<string> notes, CancellationToken cancel)
    {
        var data = file.Table(table.Name);
        if (data is null || data.Rows.Count == 0)
            return;
        if (!schema.TryGetValue(table.Name, out var columns))
        {
            notes.Add($"{Describe(table.Name)}: {data.Rows.Count} record(s) not restored, as this site's database hasn't got them yet. " +
                      "Run the site's latest database migrations, then restore again.");
            return;
        }

        var missing = data.Columns.Where(c => !columns.Any(t => t.Name.Equals(c, StringComparison.OrdinalIgnoreCase))).ToList();
        if (missing.Count > 0)
            notes.Add($"{Describe(table.Name)}: this site's database hasn't got {string.Join(", ", missing)}, so those details were left out. " +
                      "Run the site's latest database migrations, then restore again to keep them.");

        var targets = Targets(table, data, columns);
        var identity = table.Identity is null ? null : columns.FirstOrDefault(c => c.IsIdentity);
        var oldIdIndex = identity is null ? -1 : data.IndexOf(identity.Name);
        if (identity is not null && oldIdIndex < 0)
        {
            notes.Add($"{Describe(table.Name)}: not restored, as the backup has no record numbers for it.");
            return;
        }

        var siteRows = await SiteRowsAsync(con, tx, table, columns, identity, cancel);
        var ownerIndex = table.Owner is null ? -1 : data.IndexOf(table.Owner);
        var keyIndexes = table.SiteRowKey.Select(data.IndexOf).ToList();
        var rows = new List<(long? OldId, object?[] Values)>();
        var later = new List<(long OldId, Target Target, long OldValue, string? RefTable)>();
        var orphans = 0;
        foreach (var row in data.Rows)
        {
            var oldId = oldIdIndex < 0 ? null : BackupValues.ToLong(row[oldIdIndex]);

            // A site-wide row (the standard accounts): use this site's own when it has it
            if (siteRows is not null && oldId is not null && ownerIndex >= 0 && BackupValues.ToLong(row[ownerIndex]) == 0
                && siteRows.Match(keyIndexes.Select(k => k < 0 ? null : BackupValues.ToText(row[k])).ToList()) is { } siteId)
            {
                map.Add(table.Name, oldId.Value, siteId);
                continue;
            }
            var values = new object?[targets.Count];
            var keep = true;
            for (var i = 0; i < targets.Count && keep; i++)
            {
                var t = targets[i];
                var raw = t.FileIndex < 0 ? null : row[t.FileIndex];
                switch (t.Role)
                {
                    case Role.Owner:
                        values[i] = portfolioId;
                        break;
                    case Role.Login:
                        values[i] = BackupValues.ToLong(raw) is null ? null : loginId;
                        break;
                    case Role.Id:
                    {
                        var old = BackupValues.ToLong(raw);
                        if (old is null or 0)
                            values[i] = old;
                        else if (map.TryGet(t.Id!.Table, old.Value, out var id))
                            values[i] = id;
                        else if (t.Column.Nullable)
                            values[i] = null;
                        else
                            keep = false;
                        break;
                    }
                    case Role.Later or Role.Source:
                    {
                        var old = BackupValues.ToLong(raw);
                        var kind = t.KindIndex < 0 ? null : BackupValues.ToText(row[t.KindIndex]);
                        if (t.Role == Role.Source && t.Source!.Keeps(kind))
                            values[i] = old;
                        else if (old is null or 0)
                            values[i] = old is null && t.Column.Nullable ? null : 0L;
                        else
                        {
                            values[i] = t.Column.Nullable ? null : 0L;
                            if (oldId is { } rowId)
                                later.Add((rowId, t, old.Value, t.Role == Role.Later ? t.Id!.Table : t.Source!.TableFor(kind)));
                        }
                        break;
                    }
                    case Role.LineXml:
                        values[i] = BackupValues.RemapLineXml(BackupValues.ToText(raw), map);
                        break;
                    default:
                        values[i] = t.FileIndex < 0 ? BackupValues.EmptyFor(t.Column.Type) : raw;
                        break;
                }
                if (keep && values[i] is null && !t.Column.Nullable && t.Role is not (Role.Later or Role.Source))
                    values[i] = BackupValues.EmptyFor(t.Column.Type);
                values[i] = BackupValues.Convert(values[i], t.Column.Type);
            }
            if (keep)
                rows.Add((oldId, values));
            else
                orphans++;
        }

        int inserted;
        if (table.SharedKey.Count > 0)
            inserted = await InsertSharedAsync(con, tx, table, targets, rows.Select(r => r.Values).ToList(), cancel);
        else if (identity is not null)
            inserted = await InsertWithIdsAsync(con, tx, table.Name, identity.Name, targets, rows, map, cancel);
        else
            inserted = await InsertAsync(con, tx, table.Name, targets, rows.Select(r => r.Values).ToList(), cancel);

        foreach (var (oldRowId, t, oldValue, refTable) in later)
            pending.Add(new Pending(table.Name, identity!.Name, oldRowId, t.Column.Name, refTable, oldValue, t.Column.Nullable));
        counts[table.Name] = inserted;
        if (orphans > 0)
            notes.Add($"{Describe(table.Name)}: {orphans} record(s) left out, as what they belonged to isn't in the backup.");
    }

    /// <summary>This site's own rows of a table that has site-wide ones, to match a backup's against.</summary>
    private sealed class SiteRows(List<(long Id, string?[] Key)> rows)
    {
        /// <summary>The row whose key matches; failing that, the only one whose first key column matches.</summary>
        public long? Match(IReadOnlyList<string?> key)
        {
            static string Norm(string? s) => s?.Trim() ?? "";
            var exact = rows.Where(r => r.Key.Select(Norm).SequenceEqual(key.Select(Norm), StringComparer.OrdinalIgnoreCase)).ToList();
            if (exact.Count > 0)
                return exact[0].Id;
            var first = rows.Where(r => string.Equals(Norm(r.Key[0]), Norm(key[0]), StringComparison.OrdinalIgnoreCase)).ToList();
            return first.Count == 1 ? first[0].Id : null;
        }
    }

    private static async Task<SiteRows?> SiteRowsAsync(DbConnection con, DbTransaction tx, BackupTable table, List<Column> columns,
        Column? identity, CancellationToken cancel)
    {
        if (table.SiteRowKey.Count == 0 || identity is null || table.Owner is null
            || !table.SiteRowKey.All(k => columns.Any(c => c.Name.Equals(k, StringComparison.OrdinalIgnoreCase))))
            return null;
        var sql = $"SELECT CAST([{identity.Name}] AS BIGINT), {string.Join(", ", table.SiteRowKey.Select(k => $"CAST([{k}] AS NVARCHAR(400))"))} " +
                  $"FROM [{table.Name}] WHERE [{table.Owner}] = 0";
        var rows = new List<(long, string?[])>();
        await using var reader = await con.ExecuteReaderAsync(new CommandDefinition(sql, transaction: tx, cancellationToken: cancel));
        while (await reader.ReadAsync(cancel))
            rows.Add((reader.GetInt64(0), Enumerable.Range(1, table.SiteRowKey.Count).Select(i => reader.IsDBNull(i) ? null : reader.GetString(i)).ToArray()));
        return new SiteRows(rows);
    }

    /// <summary>The columns to fill and what each one is, from the plan and this database's columns.</summary>
    private static List<Target> Targets(BackupTable table, BackupTableData data, List<Column> columns)
    {
        var targets = new List<Target>();
        foreach (var c in columns.Where(c => !c.IsIdentity && !c.IsComputed))
        {
            var index = data.IndexOf(c.Name);
            var id = table.Ids.FirstOrDefault(i => i.Column.Equals(c.Name, StringComparison.OrdinalIgnoreCase));
            var source = table.Sources.FirstOrDefault(s => s.Column.Equals(c.Name, StringComparison.OrdinalIgnoreCase));
            Target? target =
                table.Owner?.Equals(c.Name, StringComparison.OrdinalIgnoreCase) == true ? new(c, index, Role.Owner)
                : table.ParentColumn?.Equals(c.Name, StringComparison.OrdinalIgnoreCase) == true
                    ? new(c, index, Role.Id, new IdColumn(c.Name, table.Parent!))
                : id is not null ? new(c, index, id.Later ? Role.Later : Role.Id, id)
                : source is not null ? new(c, index, Role.Source, Source: source, KindIndex: data.IndexOf(source.KindColumn))
                : table.LineXml.Contains(c.Name, StringComparer.OrdinalIgnoreCase) ? new(c, index, Role.LineXml)
                : BackupPlan.IsLoginColumn(c.Name) ? new(c, index, Role.Login)
                : index >= 0 || (!c.Nullable && !c.HasDefault) ? new(c, index, Role.Plain)
                : null;
            if (target is not null)
                targets.Add(target);
        }
        return targets;
    }

    private static int BatchSize(int columns) => Math.Max(1, MaxParameters / (columns + 1));

    private static void AddValues(DynamicParameters parameters, StringBuilder sql, List<Target> targets, object?[] values, int row, long? oldId = null)
    {
        sql.Append(row == 0 ? "(" : ",(");
        if (oldId is not null)
        {
            parameters.Add($"o{row}", oldId, DbType.Int64);
            sql.Append($"@o{row}");
        }
        for (var i = 0; i < targets.Count; i++)
        {
            parameters.Add($"v{row}_{i}", values[i], BackupValues.DbTypeFor(targets[i].Column.Type));
            sql.Append(oldId is null && i == 0 ? "" : ",").Append($"@v{row}_{i}");
        }
        sql.Append(')');
    }

    private static string ColumnList(List<Target> targets, string prefix = "") =>
        string.Join(", ", targets.Select(t => $"{prefix}[{t.Column.Name}]"));

    /// <summary>
    /// Inserts rows and records each one's new id against its old one. The rows go in in their old order (an INSERT
    /// with ORDER BY numbers identities in that order), so same-day trades and journals keep their order; the new ids,
    /// sorted, then line up with the old ones. OUTPUT goes INTO a table variable because of the triggers.
    /// </summary>
    private static async Task<int> InsertWithIdsAsync(DbConnection con, DbTransaction tx, string table, string identity,
        List<Target> targets, List<(long? OldId, object?[] Values)> rows, IdMap map, CancellationToken cancel)
    {
        var ordered = rows.Where(r => r.OldId is not null).GroupBy(r => r.OldId!.Value).Select(g => g.First()).OrderBy(r => r.OldId).ToList();
        var size = BatchSize(targets.Count);
        for (var start = 0; start < ordered.Count; start += size)
        {
            var batch = ordered.Skip(start).Take(size).ToList();
            var parameters = new DynamicParameters();
            var sql = new StringBuilder("DECLARE @ids TABLE (n BIGINT);\n");
            sql.Append($"INSERT INTO [{table}] ({ColumnList(targets)}) OUTPUT CAST(inserted.[{identity}] AS BIGINT) INTO @ids\n");
            sql.Append($"SELECT {string.Join(", ", targets.Select((_, i) => $"s.[c{i}]"))} FROM (VALUES ");
            for (var r = 0; r < batch.Count; r++)
                AddValues(parameters, sql, targets, batch[r].Values, r, batch[r].OldId);
            sql.Append($") AS s([__old], {string.Join(", ", targets.Select((_, i) => $"[c{i}]"))}) ORDER BY s.[__old];\n");
            sql.Append("SELECT n FROM @ids ORDER BY n;");
            var ids = (await con.QueryAsync<long>(new CommandDefinition(sql.ToString(), parameters, tx, LongTimeout, cancellationToken: cancel))).ToList();
            if (ids.Count != batch.Count)
                throw new InvalidOperationException($"{table}: {batch.Count} rows sent but {ids.Count} added.");
            for (var i = 0; i < ids.Count; i++)
                map.Add(table, batch[i].OldId!.Value, ids[i]);
        }
        return ordered.Count;
    }

    private static async Task<int> InsertAsync(DbConnection con, DbTransaction tx, string table, List<Target> targets,
        List<object?[]> rows, CancellationToken cancel)
    {
        var size = BatchSize(targets.Count);
        for (var start = 0; start < rows.Count; start += size)
        {
            var batch = rows.Skip(start).Take(size).ToList();
            var parameters = new DynamicParameters();
            var sql = new StringBuilder($"INSERT INTO [{table}] ({ColumnList(targets)}) VALUES ");
            for (var r = 0; r < batch.Count; r++)
                AddValues(parameters, sql, targets, batch[r], r);
            await con.ExecuteAsync(new CommandDefinition(sql.ToString(), parameters, tx, LongTimeout, cancellationToken: cancel));
        }
        return rows.Count;
    }

    /// <summary>Site-wide rows (share names): added one at a time, only when the site hasn't got that share yet.</summary>
    private static async Task<int> InsertSharedAsync(DbConnection con, DbTransaction tx, BackupTable table, List<Target> targets,
        List<object?[]> rows, CancellationToken cancel)
    {
        var keys = table.SharedKey.Select(k => targets.FindIndex(t => t.Column.Name.Equals(k, StringComparison.OrdinalIgnoreCase))).ToList();
        if (keys.Any(k => k < 0))
            return 0;
        var added = 0;
        var sql = $"INSERT INTO [{table.Name}] ({ColumnList(targets)}) SELECT {string.Join(", ", targets.Select((_, i) => $"@v{i}"))} " +
                  $"WHERE NOT EXISTS (SELECT 1 FROM [{table.Name}] WHERE {string.Join(" AND ", keys.Select(k => $"[{targets[k].Column.Name}] = @v{k}"))})";
        foreach (var row in rows)
        {
            var parameters = new DynamicParameters();
            for (var i = 0; i < targets.Count; i++)
                parameters.Add($"v{i}", row[i], BackupValues.DbTypeFor(targets[i].Column.Type));
            added += await con.ExecuteAsync(new CommandDefinition(sql, parameters, tx, LongTimeout, cancellationToken: cancel));
        }
        return added;
    }

    /// <summary>Sets the ids that had to wait, in batches per column. One with no match stays empty (or 0 when it must have a value).</summary>
    private static async Task FixPendingAsync(DbConnection con, DbTransaction tx, List<Pending> pending, IdMap map, CancellationToken cancel)
    {
        foreach (var group in pending.GroupBy(p => (p.Table, p.IdColumn, p.Column)))
        {
            var fixes = group
                .Select(p => (Row: map.TryGet(p.Table, p.OldRowId, out var r) ? r : (long?)null,
                              Value: p.RefTable is not null && map.TryGet(p.RefTable, p.OldValue, out var v) ? v : (long?)null))
                .Where(f => f.Row is not null && f.Value is not null)
                .ToList();
            for (var start = 0; start < fixes.Count; start += MaxParameters / 2)
            {
                var batch = fixes.Skip(start).Take(MaxParameters / 2).ToList();
                var parameters = new DynamicParameters();
                var values = string.Join(",", batch.Select((f, i) =>
                {
                    parameters.Add($"r{i}", f.Row, DbType.Int64);
                    parameters.Add($"v{i}", f.Value, DbType.Int64);
                    return $"(@r{i},@v{i})";
                }));
                var (table, idColumn, column) = group.Key;
                await con.ExecuteAsync(new CommandDefinition(
                    $"UPDATE t SET [{column}] = f.v FROM [{table}] t JOIN (VALUES {values}) AS f(r, v) ON t.[{idColumn}] = f.r",
                    parameters, tx, LongTimeout, cancellationToken: cancel));
            }
        }
    }

    /// <summary>Plain words for a table, for the notes after a restore.</summary>
    public static string Describe(string table) => table.ToLowerInvariant() switch
    {
        "tblsharetrades" => "Share trades",
        "tblpayouts" => "Payouts",
        "tblbankstatement" => "Cash transactions",
        "tbljournal" => "Journals",
        "tbljournallines" => "Journal lines",
        "tblchartofaccounts" => "Chart of accounts",
        "tblinvoices" => "Invoices",
        "tblbills" => "Bills",
        "tblattachments" => "Attached files",
        "tblrentalproperties" => "Rental properties",
        "tblshareprice" => "Prices entered by hand",
        _ => SplitWords(table.StartsWith("tbl", StringComparison.OrdinalIgnoreCase) ? table[3..] : table),
    };

    private static string SplitWords(string name)
    {
        var text = new StringBuilder();
        foreach (var c in name)
        {
            if (char.IsUpper(c) && text.Length > 0)
                text.Append(' ');
            text.Append(text.Length == 0 ? c : char.ToLower(c, CultureInfo.InvariantCulture));
        }
        return text.ToString();
    }
}
