using System.Data;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;

namespace PortfolioManager.Core.Backup;

/// <summary>The rows of one table in a backup: column names, then one array of values per row.</summary>
public sealed class BackupTableData(string name, IReadOnlyList<string> columns)
{
    public string Name { get; } = name;
    public IReadOnlyList<string> Columns { get; } = columns;

    /// <summary>Values are plain .NET values when written, and JsonElements when read back from a file.</summary>
    public List<object?[]> Rows { get; } = [];

    public int IndexOf(string column)
    {
        for (var i = 0; i < Columns.Count; i++)
            if (Columns[i].Equals(column, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }
}

/// <summary>
/// A whole portfolio as one file: gzip-compressed JSON that any copy of the site can read, whatever its ids and
/// whichever migrations it has run. Rows keep their old ids so the restore can join them up again under new ones.
/// </summary>
public sealed class BackupFile
{
    public const string FormatName = "share-portfolio-manager-backup";
    public const int CurrentVersion = 1;
    public const string Extension = ".pmbackup";

    /// <summary>Most a backup may grow to once unzipped, so a crafted file can't fill the server's memory.</summary>
    public const long MaxUnzippedBytes = 1024L * 1024 * 1024;

    public int Version { get; init; } = CurrentVersion;
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;

    /// <summary>The web address it came from, shown when restoring.</summary>
    public string? Source { get; init; }

    public string PortfolioName { get; init; } = "";
    public Dictionary<string, object?> Settings { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public List<BackupTableData> Tables { get; init; } = [];

    public BackupTableData? Table(string name) => Tables.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public int RowCount => Tables.Sum(t => t.Rows.Count);

    /// <summary>A file name like "Smith Family Trust 2026-10-29.pmbackup", without characters Windows refuses.</summary>
    public static string FileName(string portfolioName, DateTime date)
    {
        var bad = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
        var safe = new string(portfolioName.Trim().Select(c => bad.Contains(c) || char.IsControl(c) ? '-' : c).ToArray()).Trim(' ', '.');
        return $"{(safe.Length == 0 ? "Portfolio" : safe)} {date:yyyy-MM-dd}{Extension}";
    }

    public async Task WriteAsync(Stream output, CancellationToken cancel = default)
    {
        await using var zip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true);
        await using var json = new Utf8JsonWriter(zip);
        json.WriteStartObject();
        json.WriteString("format", FormatName);
        json.WriteNumber("version", Version);
        json.WriteString("created", CreatedUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
        if (Source is not null)
            json.WriteString("source", Source);
        json.WriteString("portfolio", PortfolioName);
        json.WriteStartObject("settings");
        foreach (var (key, value) in Settings)
        {
            json.WritePropertyName(key);
            WriteValue(json, value);
        }
        json.WriteEndObject();
        json.WriteStartArray("tables");
        foreach (var table in Tables)
        {
            json.WriteStartObject();
            json.WriteString("name", table.Name);
            json.WriteStartArray("columns");
            foreach (var c in table.Columns)
                json.WriteStringValue(c);
            json.WriteEndArray();
            json.WriteStartArray("rows");
            foreach (var row in table.Rows)
            {
                json.WriteStartArray();
                foreach (var value in row)
                    WriteValue(json, value);
                json.WriteEndArray();
                if (json.BytesPending > 64 * 1024)
                    await json.FlushAsync(cancel);
            }
            json.WriteEndArray();
            json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WriteEndObject();
        await json.FlushAsync(cancel);
    }

    private static void WriteValue(Utf8JsonWriter json, object? value)
    {
        switch (value)
        {
            case null or DBNull: json.WriteNullValue(); break;
            case bool b: json.WriteBooleanValue(b); break;
            case byte or short or int or long: json.WriteNumberValue(Convert.ToInt64(value, CultureInfo.InvariantCulture)); break;
            case decimal m: json.WriteNumberValue(m); break;
            case double d when double.IsFinite(d): json.WriteNumberValue(d); break;
            case float f when float.IsFinite(f): json.WriteNumberValue(f); break;
            case double or float: json.WriteNullValue(); break;
            case DateTime dt: json.WriteStringValue(dt.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture)); break;
            case DateTimeOffset dto: json.WriteStringValue(dto.ToString("O", CultureInfo.InvariantCulture)); break;
            case TimeSpan ts: json.WriteStringValue(ts.ToString("c", CultureInfo.InvariantCulture)); break;
            case byte[] bytes: json.WriteBase64StringValue(bytes); break;
            case JsonElement e: e.WriteTo(json); break;
            default: json.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture)); break;
        }
    }

    /// <summary>Reads a backup (zipped or plain JSON). Throws <see cref="BackupFormatException"/> with words for the page.</summary>
    public static async Task<BackupFile> ReadAsync(Stream input, CancellationToken cancel = default)
    {
        var buffered = new BufferedStream(input);
        var head = new byte[2];
        var n = await buffered.ReadAtLeastAsync(head, 2, throwOnEndOfStream: false, cancel);
        Stream body = new PrefixedStream(head.AsMemory(0, n).ToArray(), buffered);
        if (n == 2 && head[0] == 0x1F && head[1] == 0x8B)
            body = new GZipStream(body, CompressionMode.Decompress);

        JsonDocument doc;
        try
        {
            doc = await JsonDocument.ParseAsync(new LimitedStream(body, MaxUnzippedBytes), default, cancel);
        }
        catch (Exception e) when (e is JsonException or InvalidDataException)
        {
            throw new BackupFormatException("That file isn't a portfolio backup from this site (it couldn't be read).");
        }
        catch (LimitExceededException)
        {
            throw new BackupFormatException("That backup is too big for this site to restore in one go.");
        }

        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("format", out var format) || format.GetString() != FormatName)
            throw new BackupFormatException("That file isn't a portfolio backup from this site.");
        var version = root.TryGetProperty("version", out var v) && v.TryGetInt32(out var vi) ? vi : 0;
        if (version < 1)
            throw new BackupFormatException("That backup has no version number, so it can't be read.");
        if (version > CurrentVersion)
            throw new BackupFormatException("That backup was made by a newer version of the site. Update this copy of the site, then restore it.");

        var file = new BackupFile
        {
            Version = version,
            CreatedUtc = root.TryGetProperty("created", out var c) && DateTime.TryParse(c.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var created) ? created : DateTime.MinValue,
            Source = root.TryGetProperty("source", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null,
            PortfolioName = root.TryGetProperty("portfolio", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "",
        };
        if (root.TryGetProperty("settings", out var settings) && settings.ValueKind == JsonValueKind.Object)
            foreach (var prop in settings.EnumerateObject())
                file.Settings[prop.Name] = prop.Value;
        if (root.TryGetProperty("tables", out var tables) && tables.ValueKind == JsonValueKind.Array)
            foreach (var t in tables.EnumerateArray())
            {
                if (!t.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String
                    || !t.TryGetProperty("columns", out var cols) || cols.ValueKind != JsonValueKind.Array)
                    throw new BackupFormatException("Part of that backup is damaged (a table has no name or columns).");
                var columns = cols.EnumerateArray().Select(x => x.GetString() ?? "").ToList();
                var data = new BackupTableData(name.GetString()!, columns);
                if (t.TryGetProperty("rows", out var rows) && rows.ValueKind == JsonValueKind.Array)
                    foreach (var r in rows.EnumerateArray())
                    {
                        if (r.ValueKind != JsonValueKind.Array || r.GetArrayLength() != columns.Count)
                            throw new BackupFormatException($"Part of that backup is damaged (a row in {data.Name} has the wrong number of values).");
                        data.Rows.Add(r.EnumerateArray().Select(x => (object?)x).ToArray());
                    }
                file.Tables.Add(data);
            }
        return file;
    }

    private sealed class LimitExceededException : IOException;

    /// <summary>Stops reading once more than the limit has come through.</summary>
    private sealed class LimitedStream(Stream inner, long limit) : Stream
    {
        private long read;
        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancel = default) =>
            Count(await inner.ReadAsync(buffer, cancel));
        private int Count(int n)
        {
            read += n;
            return read > limit ? throw new LimitExceededException() : n;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => read; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Gives back the bytes already read to sniff the file type, then the rest of the stream.</summary>
    private sealed class PrefixedStream(byte[] prefix, Stream inner) : Stream
    {
        private int used;
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            if (used < prefix.Length)
            {
                var n = Math.Min(buffer.Length, prefix.Length - used);
                prefix.AsSpan(used, n).CopyTo(buffer);
                used += n;
                return n;
            }
            return inner.Read(buffer);
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancel = default)
        {
            if (used < prefix.Length)
                return Read(buffer.Span);
            return await inner.ReadAsync(buffer, cancel);
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

public sealed class BackupFormatException(string message) : Exception(message);

/// <summary>Old ids to new ones, per table, built up as a restore puts rows in.</summary>
public sealed class IdMap
{
    private readonly Dictionary<string, Dictionary<long, long>> maps = new(StringComparer.OrdinalIgnoreCase);

    public void Add(string table, long oldId, long newId)
    {
        if (!maps.TryGetValue(table, out var map))
            maps[table] = map = [];
        map[oldId] = newId;
    }

    public bool TryGet(string table, long oldId, out long newId)
    {
        newId = 0;
        return maps.TryGetValue(table, out var map) && map.TryGetValue(oldId, out newId);
    }

    public int Count(string table) => maps.TryGetValue(table, out var map) ? map.Count : 0;
}

/// <summary>Turning file values into what the database column takes, and renumbering ids.</summary>
public static class BackupValues
{
    /// <summary>The ADO.NET type for a SQL Server type name, so every parameter (null or not) has the column's type.</summary>
    public static DbType DbTypeFor(string sqlType) => sqlType.ToLowerInvariant() switch
    {
        "int" => DbType.Int32,
        "bigint" => DbType.Int64,
        "smallint" => DbType.Int16,
        "tinyint" => DbType.Byte,
        "bit" => DbType.Boolean,
        "decimal" or "numeric" or "money" or "smallmoney" => DbType.Decimal,
        "float" => DbType.Double,
        "real" => DbType.Single,
        "date" => DbType.Date,
        "datetime" or "smalldatetime" => DbType.DateTime,
        "datetime2" => DbType.DateTime2,
        "datetimeoffset" => DbType.DateTimeOffset,
        "time" => DbType.Time,
        "char" or "varchar" or "text" => DbType.AnsiString,
        "varbinary" or "binary" or "image" => DbType.Binary,
        "uniqueidentifier" => DbType.Guid,
        _ => DbType.String,
    };

    /// <summary>A whole-number id from a file or database value; null when it is empty or not a whole number.</summary>
    public static long? ToLong(object? value) => value switch
    {
        null or DBNull => null,
        JsonElement { ValueKind: JsonValueKind.Number } e => e.TryGetInt64(out var l) ? l : null,
        JsonElement { ValueKind: JsonValueKind.String } e => long.TryParse(e.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l : null,
        JsonElement => null,
        byte or short or int or long => System.Convert.ToInt64(value, CultureInfo.InvariantCulture),
        decimal m when m == Math.Truncate(m) => (long)m,
        _ => null,
    };

    public static string? ToText(object? value) => value switch
    {
        null or DBNull => null,
        JsonElement { ValueKind: JsonValueKind.Null } => null,
        JsonElement { ValueKind: JsonValueKind.String } e => e.GetString(),
        JsonElement e => e.GetRawText(),
        _ => System.Convert.ToString(value, CultureInfo.InvariantCulture),
    };

    /// <summary>A value for a column of the given SQL type, from a file (JsonElement) or from the restore itself.</summary>
    public static object? Convert(object? value, string sqlType)
    {
        if (value is JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } || value is null or DBNull)
            return null;
        var type = DbTypeFor(sqlType);
        if (value is not JsonElement e)
            return type switch
            {
                DbType.Int32 => System.Convert.ToInt32(value, CultureInfo.InvariantCulture),
                DbType.Int64 => System.Convert.ToInt64(value, CultureInfo.InvariantCulture),
                DbType.Int16 => System.Convert.ToInt16(value, CultureInfo.InvariantCulture),
                DbType.Byte => System.Convert.ToByte(value, CultureInfo.InvariantCulture),
                _ => value,
            };
        try
        {
            return type switch
            {
                DbType.Int32 => (int)Number(e),
                DbType.Int64 => (long)Number(e),
                DbType.Int16 => (short)Number(e),
                DbType.Byte => (byte)Number(e),
                DbType.Boolean => e.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Number => e.GetDecimal() != 0,
                    _ => bool.Parse(e.GetString()!),
                },
                DbType.Decimal => Number(e),
                DbType.Double => e.ValueKind == JsonValueKind.Number ? e.GetDouble() : double.Parse(e.GetString()!, CultureInfo.InvariantCulture),
                DbType.Single => e.ValueKind == JsonValueKind.Number ? e.GetSingle() : float.Parse(e.GetString()!, CultureInfo.InvariantCulture),
                DbType.Date or DbType.DateTime or DbType.DateTime2 =>
                    DateTime.Parse(e.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                DbType.DateTimeOffset => DateTimeOffset.Parse(e.GetString()!, CultureInfo.InvariantCulture),
                DbType.Time => TimeSpan.Parse(e.GetString()!, CultureInfo.InvariantCulture),
                DbType.Binary => e.GetBytesFromBase64(),
                DbType.Guid => Guid.Parse(e.GetString()!),
                _ => e.ValueKind == JsonValueKind.String ? e.GetString() : e.GetRawText(),
            };
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or OverflowException)
        {
            throw new BackupFormatException($"A value in the backup ({Short(e.GetRawText())}) doesn't fit a {sqlType} column.");
        }
    }

    private static decimal Number(JsonElement e) =>
        e.ValueKind == JsonValueKind.Number ? e.GetDecimal() : decimal.Parse(e.GetString()!, NumberStyles.Float, CultureInfo.InvariantCulture);

    private static string Short(string text) => text.Length > 40 ? text[..40] + "..." : text;

    /// <summary>A value for a NOT NULL column the backup doesn't have (it came from an older copy of the site).</summary>
    public static object EmptyFor(string sqlType) => DbTypeFor(sqlType) switch
    {
        DbType.Int32 => 0,
        DbType.Int64 => 0L,
        DbType.Int16 => (short)0,
        DbType.Byte => (byte)0,
        DbType.Boolean => false,
        DbType.Decimal => 0m,
        DbType.Double => 0d,
        DbType.Single => 0f,
        DbType.Date or DbType.DateTime or DbType.DateTime2 => DateTime.Today,
        DbType.DateTimeOffset => DateTimeOffset.Now,
        DbType.Time => TimeSpan.Zero,
        DbType.Binary => Array.Empty<byte>(),
        DbType.Guid => Guid.Empty,
        _ => "",
    };

    /// <summary>
    /// Renumbers a stored line list (repeating invoices and bills, recurring journals): a = account, t = tax code,
    /// i = item, t1 and t2 = tracking options. An id with no match is dropped rather than left pointing elsewhere.
    /// </summary>
    public static string? RemapLineXml(string? xml, IdMap map)
    {
        if (string.IsNullOrWhiteSpace(xml))
            return xml;
        XElement root;
        try
        {
            root = XElement.Parse(xml);
        }
        catch (System.Xml.XmlException)
        {
            return xml;
        }
        foreach (var line in root.Elements("l"))
            foreach (var (attr, table) in LineXmlIds)
                if (line.Attribute(attr) is { } a)
                {
                    if (long.TryParse(a.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var old) && map.TryGet(table, old, out var id))
                        a.Value = id.ToString(CultureInfo.InvariantCulture);
                    else
                        a.Remove();
                }
        return root.ToString(SaveOptions.DisableFormatting);
    }

    private static readonly (string Attr, string Table)[] LineXmlIds =
        [("a", "tblChartOfAccounts"), ("t", "tblTaxCodes"), ("i", "tblInventoryItems"), ("t1", "tblTrackingOptions"), ("t2", "tblTrackingOptions")];
}
