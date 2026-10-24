using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Jularr.Integrations.Logic.Database;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Jularr.Backend.Service.Core
{
    // The one installation and upgrade owner. An empty database receives the baselines (schema version 1). A database of the frozen baseline
    // without a version table is checked for completeness and marked version 1. A versioned database receives the missing numbered migrations
    // (database/migrations) after a backup, all in one transaction together with their version entries.
    internal partial class DatabaseBaseline : IHostedService
    {
        private readonly string _connectionString;
        private readonly BackupOptions _backup;
        private readonly PostgresBackupTools _tools;
        private readonly IReadOnlyList<DatabaseMigration> _migrations;

        // The embedded database/migrations/NNNN_name.sql scripts from version 2; the baselines (version 1) are frozen.
        internal static IReadOnlyList<DatabaseMigration> Migrations { get; } = DatabaseMigration.Embedded(typeof(DatabaseBaseline).Assembly);

        public static int CurrentVersion => 1 + Migrations.Count;
        public static string CurrentFingerprint => Fingerprint(CurrentVersion, Migrations);

        internal DatabaseBaseline(string connectionString, BackupOptions? backup = null, PostgresBackupTools? tools = null, IReadOnlyList<DatabaseMigration>? migrations = null)
        {
            _connectionString = connectionString;
            _backup = backup ?? new BackupOptions();
            _tools = tools ?? new PostgresBackupTools();
            _migrations = migrations ?? Migrations;
            for (var index = 0; index < _migrations.Count; index++)
            {
                if (_migrations[index].Version != index + 2)
                {
                    throw new InvalidDataException("Database migrations must be contiguous, starting at version 2. Embedded: " +
                        string.Join(", ", _migrations.Select(migration => migration.Name + "=" + migration.Version.ToString(global::System.Globalization.CultureInfo.InvariantCulture))));
                }
            }
        }

        [GeneratedRegex(@"\A\s*BEGIN;|COMMIT;\s*\z")]
        private static partial Regex Wrapper();

        // The order of a new installation.
        public static readonly string[] Baselines = ["Account", "Instance", "Media", "Acquisition", "Storage", "Library", "Playback", "Schema"];

        public virtual async Task StartAsync(CancellationToken cancellationToken)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            // The lock is kept while backing up, without a long database transaction: two instances never migrate at the same time.
            await using (var schemaLock = new NpgsqlCommand("SELECT pg_advisory_lock(1247104082);", connection))
            {
                await schemaLock.ExecuteNonQueryAsync(cancellationToken);
            }

            try
            {
                await UpgradeAsync(connection, cancellationToken);
            }
            finally
            {
                if (connection.State == global::System.Data.ConnectionState.Open)
                {
                    await using var unlock = new NpgsqlCommand("SELECT pg_advisory_unlock(1247104082);", connection);
                    await unlock.ExecuteNonQueryAsync(CancellationToken.None);
                }
            }
        }

        private async Task UpgradeAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
        {
            int installedVersion;
            bool legacy;
            await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
            {
                bool isEmpty;
                await using (var empty = new NpgsqlCommand("SELECT NOT EXISTS (SELECT 1 FROM pg_catalog.pg_tables WHERE schemaname = current_schema());", connection, transaction))
                {
                    isEmpty = (bool)(await empty.ExecuteScalarAsync(cancellationToken))!;
                }

                if (isEmpty)
                {
                    await InstallFreshAsync(connection, transaction, cancellationToken);
                    await ApplyMigrationsAsync(connection, transaction, 1, cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    return;
                }

                legacy = !await HasVersionTableAsync(connection, transaction, cancellationToken);
                if (legacy)
                {
                    // The baseline of an earlier build, before the version table existed: version 1 once it is complete.
                    installedVersion = 1;
                }
                else
                {
                    installedVersion = await ReadVersionAsync(connection, transaction, cancellationToken);
                    if (installedVersion == 1 + _migrations.Count)
                    {
                        return;
                    }
                }

                await transaction.CommitAsync(cancellationToken);
            }

            if (legacy)
            {
                await VerifyLegacyBaselineAsync(connection, cancellationToken);
            }

            // Nothing to migrate: no backup. Otherwise the backup comes first, and when it cannot be made the update does not run.
            if (_migrations.Any(migration => migration.Version > installedVersion))
            {
                bool requiresKeys;
                await using (var accounts = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM \"Accounts\");", connection))
                {
                    requiresKeys = (bool)(await accounts.ExecuteScalarAsync(cancellationToken))!;
                }

                try
                {
                    await BackupFiles.CreateAsync(_backup, _tools, DateTime.UtcNow, installedVersion, Fingerprint(installedVersion, _migrations), cancellationToken, requiresKeys);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    throw new DatabaseUpgradeException("backup", "The backup before the update could not be made (" + exception.Message + "). No migration was applied.", exception);
                }
            }

            await using var migrationTransaction = await connection.BeginTransactionAsync(cancellationToken);
            if (legacy)
            {
                if (await HasVersionTableAsync(connection, migrationTransaction, cancellationToken))
                {
                    throw new DatabaseUpgradeException("baseline", "The database schema changed during its pre-upgrade backup. No migrations were applied.");
                }

                await ApplyBaselineAsync(connection, migrationTransaction, "Schema", cancellationToken);
                await RecordVersionAsync(connection, migrationTransaction, 1, cancellationToken);
            }
            else if (await ReadVersionAsync(connection, migrationTransaction, cancellationToken) != installedVersion)
            {
                throw new DatabaseUpgradeException(NameOf(installedVersion), "The database schema changed during its pre-upgrade backup. No migrations were applied.");
            }

            await ApplyMigrationsAsync(connection, migrationTransaction, installedVersion, cancellationToken);
            await migrationTransaction.CommitAsync(cancellationToken);
        }

        private static async Task<bool> HasVersionTableAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
        {
            await using var marker = new NpgsqlCommand("SELECT to_regclass('\"DatabaseSchemaVersions\"') IS NOT NULL;", connection, transaction);
            return (bool)(await marker.ExecuteScalarAsync(ct))!;
        }

        // Completeness of an existing database without a version table: the baselines are installed into a scratch schema inside a transaction
        // that is rolled back, and every column of that installation (tables and views) must exist in the database. Nothing is changed.
        private static async Task VerifyLegacyBaselineAsync(NpgsqlConnection connection, CancellationToken ct)
        {
            string current;
            await using (var schema = new NpgsqlCommand("SELECT current_schema();", connection))
            {
                current = (string)(await schema.ExecuteScalarAsync(ct))!;
            }

            var missing = new List<string>();
            await using (var transaction = await connection.BeginTransactionAsync(ct))
            {
                await using (var scratch = new NpgsqlCommand("CREATE SCHEMA jularr_baseline_check; SELECT set_config('search_path', 'jularr_baseline_check,' || quote_ident(@Current) || ',public', true);", connection, transaction))
                {
                    scratch.Parameters.AddWithValue("Current", current);
                    await scratch.ExecuteNonQueryAsync(ct);
                }

                // The version table (Schema) is what such a database lacks; everything else must be there.
                foreach (var name in Baselines.Where(name => name != "Schema"))
                {
                    await ApplyBaselineAsync(connection, transaction, name, ct);
                }

                await using (var compare = new NpgsqlCommand(
                    """
                    SELECT expected.table_name || '.' || expected.column_name
                    FROM information_schema.columns expected
                    WHERE expected.table_schema = 'jularr_baseline_check'
                      AND NOT EXISTS (SELECT 1 FROM information_schema.columns actual
                                      WHERE actual.table_schema = @Current AND actual.table_name = expected.table_name
                                        AND actual.column_name = expected.column_name AND actual.udt_name = expected.udt_name)
                    ORDER BY 1;
                    """, connection, transaction))
                {
                    compare.Parameters.AddWithValue("Current", current);
                    await using var reader = await compare.ExecuteReaderAsync(ct);
                    while (await reader.ReadAsync(ct))
                    {
                        missing.Add(reader.GetString(0));
                    }
                }

                await transaction.RollbackAsync(ct);
            }

            if (missing.Count > 0)
            {
                throw new DatabaseUpgradeException("baseline", "The database has no schema version table and does not match the baseline of schema version 1 (missing: "
                    + string.Join(", ", missing.Take(10)) + (missing.Count > 10 ? ", ..." : "") + "). It was not changed.");
            }
        }

        private async Task InstallFreshAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
        {
            foreach (var name in Baselines)
            {
                try
                {
                    await ApplyBaselineAsync(connection, transaction, name, ct);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    throw new DatabaseUpgradeException("baseline " + name, exception.Message, exception);
                }
            }

            await RecordVersionAsync(connection, transaction, 1, ct);
        }

        private static async Task ApplyBaselineAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string name, CancellationToken ct)
        {
            using var reader = new StreamReader(typeof(DatabaseBaseline).Assembly.GetManifestResourceStream("Jularr.Database." + name + ".sql")!);
            var text = await reader.ReadToEndAsync(ct);
            if (Wrapper().Count(text) != 2 || text.Split("COMMIT;").Length != 2)
            {
                throw new InvalidDataException("The " + name + " baseline must be exactly one BEGIN ... COMMIT block.");
            }

            await using var baseline = new NpgsqlCommand(Wrapper().Replace(text, ""), connection, transaction);
            await baseline.ExecuteNonQueryAsync(ct);
        }

        // All pending migrations and their version entries share one transaction: a failure leaves the database exactly as it was.
        private async Task ApplyMigrationsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, int installedVersion, CancellationToken ct)
        {
            foreach (var migration in _migrations.Where(migration => migration.Version > installedVersion))
            {
                try
                {
                    await using var command = new NpgsqlCommand(migration.Statement, connection, transaction);
                    await command.ExecuteNonQueryAsync(ct);
                    await RecordVersionAsync(connection, transaction, migration.Version, ct);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    throw new DatabaseUpgradeException(migration.Name, exception.Message, exception);
                }
            }
        }

        private string NameOf(int version) => version == 1 ? "baseline"
            : version > 1 && version - 2 < _migrations.Count ? _migrations[version - 2].Name
            : "version " + version.ToString(global::System.Globalization.CultureInfo.InvariantCulture);

        private async Task<int> ReadVersionAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
        {
            await using var versions = new NpgsqlCommand("SELECT \"Version\", \"Fingerprint\" FROM \"DatabaseSchemaVersions\" ORDER BY \"Version\";", connection, transaction);
            await using var reader = await versions.ExecuteReaderAsync(ct);
            var version = 0;
            while (await reader.ReadAsync(ct))
            {
                var next = reader.GetInt32(0);
                if (next > 1 + _migrations.Count)
                {
                    throw new DatabaseUpgradeException(NameOf(next), "The database belongs to a newer Jularr release. Start a compatible image or recover the previous image and backup together.");
                }

                if (next != version + 1 || reader.GetString(1) != Fingerprint(next, _migrations))
                {
                    throw new DatabaseUpgradeException(NameOf(next), "The recorded checksum of this applied migration differs from the one in this release (a migration was changed after it was applied, or a version is missing). No schema changes were applied.");
                }

                version = next;
            }

            return version > 0 ? version : throw new DatabaseUpgradeException("baseline", "The database schema history is empty. No schema changes were applied.");
        }

        private async Task RecordVersionAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, int version, CancellationToken ct)
        {
            await using var command = new NpgsqlCommand("INSERT INTO \"DatabaseSchemaVersions\" (\"Version\", \"Name\", \"Fingerprint\") VALUES (@Version, @Name, @Fingerprint);", connection, transaction);
            command.Parameters.AddWithValue("Version", version);
            command.Parameters.AddWithValue("Name", NameOf(version));
            command.Parameters.AddWithValue("Fingerprint", Fingerprint(version, _migrations));
            await command.ExecuteNonQueryAsync(ct);
        }

        // The cumulative SHA-256 of the baselines (line endings normalized) and of every migration up to the version; version 1 is the baseline alone.
        internal static string Fingerprint(int version, IReadOnlyList<DatabaseMigration> migrations)
        {
            if (version < 1 || version > migrations.Count + 1)
            {
                throw new ArgumentOutOfRangeException(nameof(version));
            }

            var content = new StringBuilder();
            foreach (var name in Baselines)
            {
                using var reader = new StreamReader(typeof(DatabaseBaseline).Assembly.GetManifestResourceStream("Jularr.Database." + name + ".sql")!);
                content.Append(name).Append('\n').Append(reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal));
            }

            foreach (var migration in migrations.Take(version - 1))
            {
                content.Append(migration.Version.ToString(global::System.Globalization.CultureInfo.InvariantCulture)).Append('\n')
                    .Append(migration.Statement.Replace("\r\n", "\n", StringComparison.Ordinal));
            }

            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content.ToString())));
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    internal sealed partial class DatabaseMigration(int version, string name, string statement)
    {
        public int Version { get; } = version;
        public string Name { get; } = name;
        public string Statement { get; } = statement;

        [GeneratedRegex(@"\AJularr\.Database\.Migrations\.(?<name>(?<version>\d{4})_[a-z0-9]+(?:_[a-z0-9]+)*)\.sql\z")]
        private static partial Regex Resource();

        // database/migrations/NNNN_short_name.sql, embedded as Jularr.Database.Migrations.NNNN_short_name.sql; NNNN is the schema version it creates (from 0002).
        public static IReadOnlyList<DatabaseMigration> Embedded(Assembly assembly)
        {
            var migrations = new List<DatabaseMigration>();
            foreach (var resource in assembly.GetManifestResourceNames().Where(name => name.StartsWith("Jularr.Database.Migrations.", StringComparison.Ordinal)))
            {
                var match = Resource().Match(resource);
                if (!match.Success)
                {
                    throw new InvalidDataException("The embedded migration " + resource + " must be named NNNN_short_name.sql.");
                }

                using var reader = new StreamReader(assembly.GetManifestResourceStream(resource)!);
                migrations.Add(new DatabaseMigration(int.Parse(match.Groups["version"].Value, global::System.Globalization.CultureInfo.InvariantCulture), match.Groups["name"].Value, reader.ReadToEnd()));
            }

            return [.. migrations.OrderBy(migration => migration.Version)];
        }
    }

    // A failed or refused database update: the step that failed (a migration name, "backup" or "baseline") and why.
    public sealed class DatabaseUpgradeException(string migration, string reason, Exception? inner = null)
        : InvalidOperationException("Database update failed at " + migration + ": " + reason, inner)
    {
        public string Migration { get; } = migration;
        public string Reason { get; } = reason;
    }
}
