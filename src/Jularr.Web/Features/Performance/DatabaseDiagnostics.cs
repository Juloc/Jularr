using System.Data.Common;
using System.Text.RegularExpressions;
using Jularr.Web.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Jularr.Web.Features.Performance;

public sealed record DatabaseOverview(
    string Version,
    long SizeBytes,
    int ActiveConnections,
    int IdleConnections,
    int IdleInTransaction,
    int MaxConnections,
    long CommittedTransactions,
    long RolledBackTransactions,
    long BlocksHit,
    long BlocksRead,
    long TempFiles,
    long TempBytes,
    long Deadlocks,
    DateTimeOffset? StatsResetAtUtc)
{
    public double? CacheHitPercent => BlocksHit + BlocksRead == 0 ? null : BlocksHit * 100d / (BlocksHit + BlocksRead);

    public double ConnectionPercent => MaxConnections == 0 ? 0 : (ActiveConnections + IdleConnections + IdleInTransaction) * 100d / MaxConnections;
}

/// <summary>A table's size and activity. <see cref="Hint"/> is a review hint with a stable key, never an action Jularr takes.</summary>
public sealed record DatabaseTableRow(string Name, long TotalBytes, long LiveTuples, long DeadTuples, long SequentialScans, long SequentialTuplesRead, long IndexScans, DateTime? LastVacuumUtc, DateTime? LastAnalyzeUtc, string? Hint);

public sealed record DatabaseIndexRow(string Table, string Name, long SizeBytes, long Scans, bool BacksConstraint, string? Hint);

public sealed record DatabaseActivityRow(int Pid, string State, string? WaitEvent, double AgeSeconds, string Query);

public sealed record DatabaseLockWait(int BlockedPid, IReadOnlyList<int> BlockerPids, double WaitSeconds, string Query);

public sealed record DatabaseStatementRow(string Query, long Calls, double TotalMilliseconds, double MeanMilliseconds, double MaxMilliseconds, long Rows, long SharedBlocksRead, long TempBlocks);

public enum DatabaseStatementsState
{
    Available = 0,

    /// <summary>The extension is not created in this database.</summary>
    NotInstalled = 1,

    /// <summary>The extension exists but the server was not started with it in <c>shared_preload_libraries</c>.</summary>
    NotLoaded = 2,

    /// <summary>The database refused the read (insufficient privilege).</summary>
    Denied = 3
}

public enum DatabaseStatementRanking
{
    TotalTime = 0,
    MeanTime = 1,
    Calls = 2
}

public sealed record DatabaseDiagnosticsReport(
    DatabaseOverview Overview,
    IReadOnlyList<DatabaseTableRow> Tables,
    IReadOnlyList<DatabaseIndexRow> Indexes,
    bool RelationsSkipped,
    IReadOnlyList<DatabaseActivityRow> LongRunning,
    IReadOnlyList<DatabaseLockWait> LockWaits,
    DatabaseStatementsState StatementsState,
    DatabaseStatementRanking Ranking,
    IReadOnlyList<DatabaseStatementRow> Statements,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Read-only PostgreSQL evidence for Admin (#859): where the database is slow, blocked, large or short of vacuum. Everything is one bounded
/// read in a <c>READ ONLY</c> transaction with a statement timeout; nothing is written, no statement is explained or run, and query text is
/// shown only with its literals removed. Statement statistics need <c>pg_stat_statements</c>; without it that section says what is missing
/// instead of failing the page.
/// </summary>
public sealed partial class DatabaseDiagnosticsService(AppDbContext db)
{
    public const int RowLimit = 15;
    public const int QueryTextLimit = 300;
    private const string StatementTimeout = "5s";

    public async Task<DatabaseDiagnosticsReport> ReadAsync(DatabaseStatementRanking ranking, CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        var openedHere = connection.State != System.Data.ConnectionState.Open;
        if (openedHere)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await Execute(connection, transaction, "SET TRANSACTION READ ONLY", cancellationToken);
            await Execute(connection, transaction, $"SET LOCAL statement_timeout = '{StatementTimeout}'", cancellationToken);

            var overview = await ReadOverviewAsync(connection, transaction, cancellationToken);
            var longRunning = await ReadLongRunningAsync(connection, transaction, cancellationToken);
            var lockWaits = await ReadLockWaitsAsync(connection, transaction, cancellationToken);
            var (tables, indexes, skipped) = await ReadRelationsAsync(connection, transaction, cancellationToken);
            var (state, statements) = await ReadStatementsAsync(connection, transaction, ranking, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new DatabaseDiagnosticsReport(overview, tables, indexes, skipped, longRunning, lockWaits, state, ranking, statements, Warnings(overview, tables, longRunning, lockWaits));
        }
        finally
        {
            if (openedHere)
            {
                await connection.CloseAsync();
            }
        }
    }

    /// <summary>Removes string literals and long numbers, collapses whitespace and bounds the length: no bind value or secret survives.</summary>
    public static string Sanitize(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return string.Empty;
        }

        var text = Whitespace().Replace(Numbers().Replace(Literals().Replace(query, "'?'"), "?"), " ").Trim();
        return text.Length <= QueryTextLimit ? text : string.Concat(text.AsSpan(0, QueryTextLimit), "…");
    }

    public static string? TableHint(long total, long live, long dead, long sequentialTuplesRead, long indexScans, DateTime? lastAnalyze)
    {
        if (dead >= 1_000 && dead * 5 > live + dead)
        {
            return "deadTuples";
        }

        if (lastAnalyze is null && live >= 1_000)
        {
            return "neverAnalyzed";
        }

        return live >= 10_000 && sequentialTuplesRead > live * 100 && indexScans < sequentialTuplesRead / 1_000 ? "sequentialReads" : null;
    }

    public static string? IndexHint(long sizeBytes, long scans, bool backsConstraint) =>
        !backsConstraint && scans == 0 && sizeBytes >= 1_048_576 ? "unused" : null;

    private static IReadOnlyList<string> Warnings(DatabaseOverview overview, IReadOnlyList<DatabaseTableRow> tables, IReadOnlyList<DatabaseActivityRow> longRunning, IReadOnlyList<DatabaseLockWait> lockWaits)
    {
        var warnings = new List<string>();
        if (overview.ConnectionPercent >= 80)
        {
            warnings.Add("connections");
        }

        if (overview.CacheHitPercent is < 90 && overview.BlocksHit + overview.BlocksRead >= 100_000)
        {
            warnings.Add("cacheHit");
        }

        if (overview.Deadlocks > 0)
        {
            warnings.Add("deadlocks");
        }

        if (overview.IdleInTransaction > 0 || longRunning.Any(row => row.State.StartsWith("idle in transaction", StringComparison.Ordinal) && row.AgeSeconds > 30))
        {
            warnings.Add("idleInTransaction");
        }

        if (lockWaits.Count > 0)
        {
            warnings.Add("lockWaits");
        }

        if (tables.Any(row => row.Hint is not null))
        {
            warnings.Add("tables");
        }

        return warnings;
    }

    private static async Task Execute(DbConnection connection, DbTransaction transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<List<T>> Query<T>(DbConnection connection, DbTransaction transaction, string sql, Func<DbDataReader, T> map, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<T>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(map(reader));
        }

        return rows;
    }

    private static async Task<DatabaseOverview> ReadOverviewAsync(DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken)
    {
        var stats = await Query(
            connection,
            transaction,
            """
            SELECT current_setting('server_version'), pg_database_size(current_database()), d.xact_commit, d.xact_rollback, d.blks_hit, d.blks_read,
                   d.temp_files, d.temp_bytes, d.deadlocks, d.stats_reset
            FROM pg_stat_database d WHERE d.datname = current_database()
            """,
            reader => (Version: reader.GetString(0).Split(' ')[0], Size: reader.GetInt64(1), Commit: reader.GetInt64(2), Rollback: reader.GetInt64(3), Hit: reader.GetInt64(4), Read: reader.GetInt64(5),
                TempFiles: reader.GetInt64(6), TempBytes: reader.GetInt64(7), Deadlocks: reader.GetInt64(8), Reset: reader.IsDBNull(9) ? (DateTimeOffset?)null : new DateTimeOffset(reader.GetDateTime(9).ToUniversalTime())),
            cancellationToken);
        var connections = await Query(
            connection,
            transaction,
            """
            SELECT count(*) FILTER (WHERE state = 'active'), count(*) FILTER (WHERE state = 'idle'), count(*) FILTER (WHERE state LIKE 'idle in transaction%'),
                   current_setting('max_connections')::int
            FROM pg_stat_activity WHERE datname = current_database() AND backend_type = 'client backend'
            """,
            reader => (Active: (int)reader.GetInt64(0), Idle: (int)reader.GetInt64(1), IdleInTransaction: (int)reader.GetInt64(2), Max: reader.GetInt32(3)),
            cancellationToken);
        var first = stats[0];
        var counts = connections[0];
        return new DatabaseOverview(first.Version, first.Size, counts.Active, counts.Idle, counts.IdleInTransaction, counts.Max, first.Commit, first.Rollback, first.Hit, first.Read, first.TempFiles, first.TempBytes, first.Deadlocks, first.Reset);
    }

    /// <summary>
    /// Sizes need a share lock on every relation, which waits behind an exclusive lock (a migration, a rewrite). The reads therefore run in
    /// a savepoint with a short lock timeout and are skipped, not waited for, while a table is locked: that is exactly when this page is opened.
    /// </summary>
    private static async Task<(List<DatabaseTableRow> Tables, List<DatabaseIndexRow> Indexes, bool Skipped)> ReadRelationsAsync(DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken)
    {
        await Execute(connection, transaction, "SAVEPOINT relations", cancellationToken);
        try
        {
            await Execute(connection, transaction, "SET LOCAL lock_timeout = '1s'", cancellationToken);
            var tables = await ReadTablesAsync(connection, transaction, cancellationToken);
            var indexes = await ReadIndexesAsync(connection, transaction, cancellationToken);
            await Execute(connection, transaction, "RELEASE SAVEPOINT relations", cancellationToken);
            return (tables, indexes, false);
        }
        catch (PostgresException exception) when (exception.SqlState is PostgresErrorCodes.LockNotAvailable or PostgresErrorCodes.QueryCanceled)
        {
            await Execute(connection, transaction, "ROLLBACK TO SAVEPOINT relations", cancellationToken);
            return ([], [], true);
        }
    }

    private static Task<List<DatabaseTableRow>> ReadTablesAsync(DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken) => Query(
        connection,
        transaction,
        $"""
        SELECT s.relname, pg_total_relation_size(s.relid), s.n_live_tup, s.n_dead_tup, s.seq_scan, s.seq_tup_read, COALESCE(s.idx_scan, 0),
               GREATEST(s.last_vacuum, s.last_autovacuum), GREATEST(s.last_analyze, s.last_autoanalyze)
        FROM pg_stat_user_tables s ORDER BY pg_total_relation_size(s.relid) DESC LIMIT {RowLimit}
        """,
        reader =>
        {
            var total = reader.GetInt64(1);
            var live = reader.GetInt64(2);
            var dead = reader.GetInt64(3);
            var sequentialRead = reader.GetInt64(5);
            var indexScans = reader.GetInt64(6);
            var analyzed = reader.IsDBNull(8) ? (DateTime?)null : reader.GetDateTime(8);
            return new DatabaseTableRow(reader.GetString(0), total, live, dead, reader.GetInt64(4), sequentialRead, indexScans, reader.IsDBNull(7) ? null : reader.GetDateTime(7), analyzed, TableHint(total, live, dead, sequentialRead, indexScans, analyzed));
        },
        cancellationToken);

    private static Task<List<DatabaseIndexRow>> ReadIndexesAsync(DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken) => Query(
        connection,
        transaction,
        $"""
        SELECT s.relname, s.indexrelname, pg_relation_size(s.indexrelid), s.idx_scan, (i.indisunique OR i.indisprimary)
        FROM pg_stat_user_indexes s JOIN pg_index i ON i.indexrelid = s.indexrelid
        ORDER BY pg_relation_size(s.indexrelid) DESC LIMIT {RowLimit}
        """,
        reader =>
        {
            var size = reader.GetInt64(2);
            var scans = reader.GetInt64(3);
            var constraint = reader.GetBoolean(4);
            return new DatabaseIndexRow(reader.GetString(0), reader.GetString(1), size, scans, constraint, IndexHint(size, scans, constraint));
        },
        cancellationToken);

    private static async Task<IReadOnlyList<DatabaseActivityRow>> ReadLongRunningAsync(DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken) => (await Query(
        connection,
        transaction,
        """
        SELECT pid, state, wait_event, EXTRACT(EPOCH FROM now() - COALESCE(xact_start, query_start))::float8, query
        FROM pg_stat_activity
        WHERE datname = current_database() AND pid <> pg_backend_pid() AND state IS NOT NULL AND state <> 'idle'
          AND COALESCE(xact_start, query_start) < now() - interval '1 second'
        ORDER BY COALESCE(xact_start, query_start) LIMIT 10
        """,
        reader => new DatabaseActivityRow(reader.GetInt32(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetDouble(3), Sanitize(reader.IsDBNull(4) ? null : reader.GetString(4))),
        cancellationToken));

    private static async Task<IReadOnlyList<DatabaseLockWait>> ReadLockWaitsAsync(DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken) => await Query(
        connection,
        transaction,
        """
        SELECT pid, pg_blocking_pids(pid), EXTRACT(EPOCH FROM now() - query_start)::float8, query
        FROM pg_stat_activity
        WHERE datname = current_database() AND cardinality(pg_blocking_pids(pid)) > 0
        ORDER BY query_start LIMIT 10
        """,
        reader => new DatabaseLockWait(reader.GetInt32(0), reader.GetFieldValue<int[]>(1), reader.GetDouble(2), Sanitize(reader.IsDBNull(3) ? null : reader.GetString(3))),
        cancellationToken);

    private static async Task<(DatabaseStatementsState State, IReadOnlyList<DatabaseStatementRow> Rows)> ReadStatementsAsync(
        DbConnection connection,
        DbTransaction transaction,
        DatabaseStatementRanking ranking,
        CancellationToken cancellationToken)
    {
        var installed = await Query(connection, transaction, "SELECT 1 FROM pg_extension WHERE extname = 'pg_stat_statements'", reader => reader.GetInt32(0), cancellationToken);
        if (installed.Count == 0)
        {
            return (DatabaseStatementsState.NotInstalled, []);
        }

        var order = ranking switch
        {
            DatabaseStatementRanking.MeanTime => "mean_exec_time",
            DatabaseStatementRanking.Calls => "calls",
            _ => "total_exec_time"
        };

        // A failed statement aborts the surrounding transaction, so the probe runs in its own savepoint and leaves the rest of the report intact.
        await Execute(connection, transaction, "SAVEPOINT statements", cancellationToken);
        try
        {
            var rows = await Query(
                connection,
                transaction,
                $"""
                SELECT query, calls, total_exec_time, mean_exec_time, max_exec_time, rows, shared_blks_read, temp_blks_read + temp_blks_written
                FROM pg_stat_statements WHERE dbid = (SELECT oid FROM pg_database WHERE datname = current_database())
                ORDER BY {order} DESC LIMIT 10
                """,
                reader => new DatabaseStatementRow(Sanitize(reader.GetString(0)), reader.GetInt64(1), reader.GetDouble(2), reader.GetDouble(3), reader.GetDouble(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7)),
                cancellationToken);
            await Execute(connection, transaction, "RELEASE SAVEPOINT statements", cancellationToken);
            return (DatabaseStatementsState.Available, rows);
        }
        catch (PostgresException exception) when (exception.SqlState is PostgresErrorCodes.ObjectNotInPrerequisiteState or PostgresErrorCodes.InsufficientPrivilege)
        {
            await Execute(connection, transaction, "ROLLBACK TO SAVEPOINT statements", cancellationToken);
            return (exception.SqlState == PostgresErrorCodes.InsufficientPrivilege ? DatabaseStatementsState.Denied : DatabaseStatementsState.NotLoaded, []);
        }
    }

    [GeneratedRegex("'(?:[^']|'')*'")]
    private static partial Regex Literals();

    [GeneratedRegex(@"\b\d{4,}\b")]
    private static partial Regex Numbers();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
