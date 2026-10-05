using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Jularr.Web.Data.SqliteImport;

/// <summary>
/// One-time import of a legacy SQLite database (the previous persistence epoch) into the canonical
/// PostgreSQL database (#570). It runs at startup after migrations, only when the PostgreSQL database
/// is still empty and a legacy SQLite file is present under the data volume, then renames the legacy
/// file so it is never imported twice.
///
/// The copy is metadata-driven: every PostgreSQL table is filled from the same-named SQLite table in
/// foreign-key dependency order, converting SQLite's dynamically-typed values (text GUIDs, ISO-8601
/// timestamps, 0/1 booleans) to the native PostgreSQL column types. The whole import runs in one
/// transaction, so an interrupted upgrade leaves the target empty and can be retried.
///
/// This is the only place the app still reads SQLite; there is no dual-write or runtime fallback.
/// </summary>
public static class SqliteToPostgresImporter
{
    public static async Task<int> RunIfNeededAsync(
        AppDbContext db,
        IConfiguration configuration,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        var legacyPath = ResolveLegacyPath(configuration);
        if (legacyPath is null)
        {
            return 0;
        }

        if (await HasExistingDataAsync(db, cancellationToken))
        {
            log?.Invoke(
                $"A legacy SQLite database exists at '{legacyPath}', but the PostgreSQL database already " +
                "contains data; skipping import. Move or delete the legacy file to silence this message.");
            return 0;
        }

        log?.Invoke($"Importing legacy SQLite database '{legacyPath}' into PostgreSQL.");

        var tables = await OrderedTablesAsync(db, cancellationToken);

        var pgConnection = (NpgsqlConnection)db.Database.GetDbConnection();
        var openedHere = pgConnection.State != System.Data.ConnectionState.Open;
        if (openedHere)
        {
            await pgConnection.OpenAsync(cancellationToken);
        }

        var totalRows = 0;
        try
        {
            var sqliteConnectionString = new SqliteConnectionStringBuilder
            {
                DataSource = legacyPath,
                Mode = SqliteOpenMode.ReadOnly,
                // No pooling so the file handle is released as soon as the connection closes and the
                // imported legacy file can be renamed.
                Pooling = false
            }.ConnectionString;

            await using var sqlite = new SqliteConnection(sqliteConnectionString);
            await sqlite.OpenAsync(cancellationToken);

            var sqliteTables = await ReadSqliteTableNamesAsync(sqlite, cancellationToken);

            await using var transaction = await pgConnection.BeginTransactionAsync(cancellationToken);

            foreach (var table in tables)
            {
                if (!sqliteTables.Contains(table.Name))
                {
                    continue;
                }

                if (table.Name == "NotificationSubscriptions")
                {
                    var sourceColumns = await ReadSqliteColumnsAsync(sqlite, table.Name, cancellationToken);
                    if (sourceColumns.Contains("Mode"))
                    {
                        totalRows += await CopyLegacyNotificationSubscriptionsAsync(sqlite, pgConnection, transaction, cancellationToken);
                        continue;
                    }
                }

                totalRows += await CopyTableAsync(sqlite, pgConnection, transaction, table, cancellationToken);
            }

            // Identity sequences must continue past the highest imported id.
            foreach (var table in tables)
            {
                await ResetIdentityAsync(pgConnection, transaction, table, cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            if (openedHere)
            {
                await pgConnection.CloseAsync();
            }
        }

        ArchiveLegacyFile(legacyPath, log);
        log?.Invoke($"Imported {totalRows} row(s) from the legacy SQLite database into PostgreSQL.");
        return totalRows;
    }

    private static string? ResolveLegacyPath(IConfiguration configuration)
    {
        var configured = configuration["Import:LegacySqlitePath"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return File.Exists(configured) ? configured : null;
        }

        foreach (var candidate in new[] { "/data/jularr.db", "/data/anilingo.db" })
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static async Task<bool> HasExistingDataAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        // The owner account and library roots are created early in any real installation; if either
        // table already holds rows the PostgreSQL database is in use and must not be overwritten.
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var openedHere = connection.State != System.Data.ConnectionState.Open;
        if (openedHere)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT EXISTS (SELECT 1 FROM "OwnerAccounts")
                    OR EXISTS (SELECT 1 FROM "LibraryRoots")
                    OR EXISTS (SELECT 1 FROM "NovelWorks");
                """;
            var result = await command.ExecuteScalarAsync(cancellationToken);
            return result is bool exists && exists;
        }
        finally
        {
            if (openedHere)
            {
                await connection.CloseAsync();
            }
        }
    }

    private static async Task<int> CopyTableAsync(
        SqliteConnection sqlite,
        NpgsqlConnection pg,
        NpgsqlTransaction transaction,
        TableInfo table,
        CancellationToken cancellationToken)
    {
        var sourceColumns = await ReadSqliteColumnsAsync(sqlite, table.Name, cancellationToken);
        var columns = table.Columns.Where(c => sourceColumns.Contains(c.Name)).ToArray();
        if (columns.Length == 0)
        {
            return 0;
        }

        await using var read = sqlite.CreateCommand();
        var quotedSource = string.Join(", ", columns.Select(c => $"\"{c.Name}\""));
        read.CommandText = $"SELECT {quotedSource} FROM \"{table.Name}\";";

        var quotedTarget = string.Join(", ", columns.Select(c => $"\"{c.Name}\""));
        var parameters = string.Join(", ", columns.Select((_, i) => $"@p{i}"));
        var insertText = $"INSERT INTO \"{table.Name}\" ({quotedTarget}) VALUES ({parameters});";

        var rows = 0;
        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            await using var insert = pg.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = insertText;
            for (var i = 0; i < columns.Length; i++)
            {
                var raw = reader.IsDBNull(i) ? null : reader.GetValue(i);
                insert.Parameters.AddWithValue($"@p{i}", ConvertValue(raw, columns[i].DataType) ?? DBNull.Value);
            }

            await insert.ExecuteNonQueryAsync(cancellationToken);
            rows++;
        }

        return rows;
    }

    private static async Task<int> CopyLegacyNotificationSubscriptionsAsync(SqliteConnection sqlite, NpgsqlConnection pg, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using var read = sqlite.CreateCommand();
        read.CommandText =
            """
            SELECT "ProfileId", "Category", "Mode", "UpdatedAtUtc"
            FROM "NotificationSubscriptions";
            """;

        var rows = 0;
        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var profileId = reader.GetString(0);
            var category = Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture);
            var legacyMode = Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture);
            var updatedAtUtc = ParseTimestampUtc(reader.GetValue(3));
            var (enabled, timing, channel) = legacyMode switch
            {
                LegacyNotificationModeOff => (false, NotificationTimingImmediate, NotificationChannelInApp),
                LegacyNotificationModeInApp => (true, NotificationTimingImmediate, NotificationChannelInApp),
                LegacyNotificationModePush => (true, NotificationTimingImmediate, NotificationChannelPush),
                LegacyNotificationModeDigest => (false, NotificationTimingDigest, NotificationChannelInApp),
                _ => throw new InvalidDataException($"Unsupported legacy notification mode {legacyMode} for profile '{profileId}'.")
            };

            await using (var preference = pg.CreateCommand())
            {
                preference.Transaction = transaction;
                preference.CommandText =
                    """
                    INSERT INTO "NotificationSubscriptions" ("ProfileId", "Category", "Enabled", "Timing", "UpdatedAtUtc")
                    VALUES (@profileId, @category, @enabled, @timing, @updatedAtUtc);
                    """;
                preference.Parameters.AddWithValue("@profileId", profileId);
                preference.Parameters.AddWithValue("@category", category);
                preference.Parameters.AddWithValue("@enabled", enabled);
                preference.Parameters.AddWithValue("@timing", timing);
                preference.Parameters.AddWithValue("@updatedAtUtc", updatedAtUtc);
                await preference.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var selectedChannel = pg.CreateCommand())
            {
                selectedChannel.Transaction = transaction;
                selectedChannel.CommandText =
                    """
                    INSERT INTO "NotificationSubscriptionChannels" ("ProfileId", "Category", "Channel")
                    VALUES (@profileId, @category, @channel);
                    """;
                selectedChannel.Parameters.AddWithValue("@profileId", profileId);
                selectedChannel.Parameters.AddWithValue("@category", category);
                selectedChannel.Parameters.AddWithValue("@channel", channel);
                await selectedChannel.ExecuteNonQueryAsync(cancellationToken);
            }

            if (legacyMode == LegacyNotificationModePush)
            {
                await using var profileChannel = pg.CreateCommand();
                profileChannel.Transaction = transaction;
                profileChannel.CommandText =
                    """
                    INSERT INTO "NotificationProfileChannels" ("ProfileId", "Channel", "Enabled", "UpdatedAtUtc")
                    VALUES (@profileId, @channel, TRUE, @updatedAtUtc)
                    ON CONFLICT ("ProfileId", "Channel") DO UPDATE SET
                        "Enabled" = TRUE,
                        "UpdatedAtUtc" = excluded."UpdatedAtUtc";
                    """;
                profileChannel.Parameters.AddWithValue("@profileId", profileId);
                profileChannel.Parameters.AddWithValue("@channel", NotificationChannelPush);
                profileChannel.Parameters.AddWithValue("@updatedAtUtc", updatedAtUtc);
                await profileChannel.ExecuteNonQueryAsync(cancellationToken);
            }

            rows++;
        }

        return rows;
    }

    private static object? ConvertValue(object? value, string dataType)
    {
        if (value is null or DBNull)
        {
            return null;
        }

        switch (dataType)
        {
            case "uuid":
                return value is Guid guid ? guid : Guid.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!);
            case "boolean":
                return value switch
                {
                    bool b => b,
                    long l => l != 0,
                    int n => n != 0,
                    string s => s is "1" or "true" or "TRUE" or "True",
                    _ => Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0
                };
            case "timestamp with time zone":
            case "timestamp without time zone":
                return ParseTimestampUtc(value);
            case "date":
                return DateOnly.FromDateTime(ParseTimestampUtc(value));
            case "smallint":
            case "integer":
                return Convert.ToInt32(value, CultureInfo.InvariantCulture);
            case "bigint":
                return Convert.ToInt64(value, CultureInfo.InvariantCulture);
            case "double precision":
            case "real":
                return Convert.ToDouble(value, CultureInfo.InvariantCulture);
            case "numeric":
                return Convert.ToDecimal(value, CultureInfo.InvariantCulture);
            default:
                // text, character varying, bytea, jsonb (as text) and anything else pass through.
                return value;
        }
    }

    private static DateTime ParseTimestampUtc(object value)
    {
        if (value is DateTime dt)
        {
            return DateTime.SpecifyKind(dt.ToUniversalTime(), DateTimeKind.Utc);
        }

        var text = Convert.ToString(value, CultureInfo.InvariantCulture)!;
        var parsed = DateTimeOffset.Parse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        return parsed.UtcDateTime;
    }

    private static async Task ResetIdentityAsync(
        NpgsqlConnection pg,
        NpgsqlTransaction transaction,
        TableInfo table,
        CancellationToken cancellationToken)
    {
        if (table.IdentityColumn is null)
        {
            return;
        }

        await using var command = pg.CreateCommand();
        command.Transaction = transaction;
        // Advance the identity sequence to one past the largest imported value.
        command.CommandText =
            $"""
            SELECT setval(
                pg_get_serial_sequence('"{table.Name}"', '{table.IdentityColumn}'),
                COALESCE((SELECT MAX("{table.IdentityColumn}") FROM "{table.Name}"), 0) + 1,
                false);
            """;
        await command.ExecuteScalarAsync(cancellationToken);
    }

    private static async Task<IReadOnlyList<TableInfo>> OrderedTablesAsync(
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var openedHere = connection.State != System.Data.ConnectionState.Open;
        if (openedHere)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            var columns = new Dictionary<string, List<ColumnInfo>>(StringComparer.Ordinal);
            await using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    SELECT c.table_name, c.column_name, c.data_type, c.ordinal_position,
                           (c.is_identity = 'YES') AS is_identity
                    FROM information_schema.columns c
                    JOIN information_schema.tables t
                        ON t.table_schema = c.table_schema AND t.table_name = c.table_name
                    WHERE c.table_schema = 'public'
                      AND t.table_type = 'BASE TABLE'
                      AND c.table_name <> '__EFMigrationsHistory'
                    ORDER BY c.table_name, c.ordinal_position;
                    """;
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var tableName = reader.GetString(0);
                    if (!columns.TryGetValue(tableName, out var list))
                    {
                        list = [];
                        columns[tableName] = list;
                    }

                    list.Add(new ColumnInfo(reader.GetString(1), reader.GetString(2), reader.GetBoolean(4)));
                }
            }

            var edges = new List<(string Child, string Parent)>();
            await using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    SELECT tc.table_name AS child, ccu.table_name AS parent
                    FROM information_schema.table_constraints tc
                    JOIN information_schema.constraint_column_usage ccu
                        ON tc.constraint_name = ccu.constraint_name
                       AND tc.table_schema = ccu.table_schema
                    WHERE tc.constraint_type = 'FOREIGN KEY'
                      AND tc.table_schema = 'public';
                    """;
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var child = reader.GetString(0);
                    var parent = reader.GetString(1);
                    if (!string.Equals(child, parent, StringComparison.Ordinal))
                    {
                        edges.Add((child, parent));
                    }
                }
            }

            var ordered = TopologicalSort(columns.Keys, edges);
            return ordered
                .Select(name => new TableInfo(
                    name,
                    columns[name],
                    columns[name].FirstOrDefault(c => c.IsIdentity)?.Name))
                .ToArray();
        }
        finally
        {
            if (openedHere)
            {
                await connection.CloseAsync();
            }
        }
    }

    private static List<string> TopologicalSort(
        IEnumerable<string> nodes,
        IReadOnlyCollection<(string Child, string Parent)> edges)
    {
        var all = nodes.ToHashSet(StringComparer.Ordinal);
        var parents = all.ToDictionary(
            n => n,
            n => edges.Where(e => e.Child == n).Select(e => e.Parent).Where(all.Contains).ToHashSet(StringComparer.Ordinal),
            StringComparer.Ordinal);

        var result = new List<string>();
        var resolved = new HashSet<string>(StringComparer.Ordinal);
        // Deterministic order for reproducibility; break cycles defensively.
        var pending = all.OrderBy(x => x, StringComparer.Ordinal).ToList();
        while (pending.Count > 0)
        {
            var progressed = false;
            foreach (var node in pending.ToList())
            {
                if (parents[node].All(resolved.Contains))
                {
                    result.Add(node);
                    resolved.Add(node);
                    pending.Remove(node);
                    progressed = true;
                }
            }

            if (!progressed)
            {
                // Cycle (should not happen with the current schema): append the rest in name order.
                result.AddRange(pending);
                break;
            }
        }

        return result;
    }

    private static async Task<HashSet<string>> ReadSqliteTableNamesAsync(
        SqliteConnection sqlite,
        CancellationToken cancellationToken)
    {
        await using var command = sqlite.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table';";
        var names = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static async Task<HashSet<string>> ReadSqliteColumnsAsync(
        SqliteConnection sqlite,
        string table,
        CancellationToken cancellationToken)
    {
        await using var command = sqlite.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table.Replace("\"", "\"\"")}\");";
        var names = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            names.Add(reader.GetString(1));
        }

        return names;
    }

    private static void ArchiveLegacyFile(string legacyPath, Action<string>? log)
    {
        try
        {
            var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
            var archived = $"{legacyPath}.imported-{stamp}";
            File.Move(legacyPath, archived);
            foreach (var suffix in new[] { "-wal", "-shm" })
            {
                if (File.Exists(legacyPath + suffix))
                {
                    File.Move(legacyPath + suffix, archived + suffix);
                }
            }

            log?.Invoke($"Renamed the imported legacy database to '{archived}'.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            log?.Invoke(
                $"Imported the legacy database but could not rename '{legacyPath}'. Remove it manually so it " +
                "is not imported again.");
        }
    }

    private const int LegacyNotificationModeOff = 0;
    private const int LegacyNotificationModeInApp = 1;
    private const int LegacyNotificationModePush = 2;
    private const int LegacyNotificationModeDigest = 3;
    private const int NotificationChannelInApp = 1;
    private const int NotificationChannelPush = 2;
    private const int NotificationTimingImmediate = 1;
    private const int NotificationTimingDigest = 2;

    private sealed record ColumnInfo(string Name, string DataType, bool IsIdentity);

    private sealed record TableInfo(string Name, IReadOnlyList<ColumnInfo> Columns, string? IdentityColumn);
}
