using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Jularr.Integrations.Logic.Database;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Jularr.Backend.Service.Core
{
    // Unversioned pre-release databases retain their existing startup behavior.
    // Versioned release schemas share one installation and upgrade owner.
    internal sealed partial class DatabaseBaseline : IHostedService
    {
        private readonly string _connectionString;
        private readonly BackupOptions _backup;
        private readonly PostgresBackupTools _tools;
        private readonly IReadOnlyList<DatabaseMigration> _migrations;

        // Releases add their embedded upgrade scripts here from version 2; until the first release the baselines change instead.
        internal static IReadOnlyList<DatabaseMigration> Migrations { get; } =
        [

            new(2, """
                INSERT INTO "AcquisitionConnectionTypes" ("Id", "Key", "AcquisitionConnectionRoleId")
                    VALUES (9, 'Narou', 1);

                DO $migration$
                DECLARE
                    key_constraint text;
                    identity_constraint text;
                BEGIN
                    SELECT conname INTO key_constraint
                    FROM pg_constraint
                    WHERE conrelid = '"AcquisitionConnections"'::regclass AND contype = 'c'
                      AND position('ProtectedApiKey' in pg_get_constraintdef(oid)) > 0
                      AND position('IS NOT NULL' in pg_get_constraintdef(oid)) > 0
                      AND position('AcquisitionConnectionTypeId' in pg_get_constraintdef(oid)) > 0
                    LIMIT 1;
                    IF key_constraint IS NULL THEN
                        RAISE EXCEPTION 'Acquisition connection key constraint not found';
                    END IF;
                    EXECUTE format('ALTER TABLE "AcquisitionConnections" DROP CONSTRAINT %I', key_constraint);

                    SELECT conname INTO identity_constraint
                    FROM pg_constraint
                    WHERE conrelid = '"WorkExternalIdentities"'::regclass AND contype = 'c'
                      AND position('Namespace' in pg_get_constraintdef(oid)) > 0
                      AND position('anilist' in pg_get_constraintdef(oid)) > 0
                    LIMIT 1;
                    IF identity_constraint IS NULL THEN
                        RAISE EXCEPTION 'Work identity namespace constraint not found';
                    END IF;
                    EXECUTE format('ALTER TABLE "WorkExternalIdentities" DROP CONSTRAINT %I', identity_constraint);
                END $migration$;

                ALTER TABLE "AcquisitionConnections"
                    ADD CONSTRAINT "CK_AcquisitionConnections_NarouCredentials"
                    CHECK ("AcquisitionConnectionTypeId" IN (1, 3, 4, 5, 6, 9) OR "ProtectedApiKey" IS NOT NULL);
                ALTER TABLE "AcquisitionConnections"
                    ADD CONSTRAINT "CK_AcquisitionConnections_NarouHttps"
                    CHECK ("AcquisitionConnectionTypeId" <> 9 OR "BaseUrl" ~ '^https://');

                ALTER TABLE "WorkExternalIdentities"
                    ADD CONSTRAINT "CK_WorkExternalIdentities_NarouNamespace"
                    CHECK ("Namespace" IN ('tmdb_movie', 'tmdb_tv', 'tvdb', 'imdb', 'plex', 'openlibrary',
                        'isbn', 'gutenberg', 'anilist', 'musicbrainz', 'igdb', 'syosetu'));
                """)
        ];

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
                    throw new InvalidDataException("Database migrations must be contiguous, starting at version 2.");
                }
            }
        }

        [GeneratedRegex(@"\A\s*BEGIN;|COMMIT;\s*\z")]
        private static partial Regex Wrapper();

        // The order of a new installation.
        public static readonly string[] Baselines = ["Account", "Instance", "Media", "Acquisition", "Storage", "Library", "Playback", "Schema"];

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            // Keep the startup lock while backing up, without a long database transaction.
            await using (var schemaLock = new NpgsqlCommand("SELECT pg_advisory_lock(1247104082);", connection))
            {
                await schemaLock.ExecuteNonQueryAsync(cancellationToken);
            }

            try
            {
                int installedVersion;
                bool requiresKeys;
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

                    await using var marker = new NpgsqlCommand("SELECT to_regclass('\"DatabaseSchemaVersions\"') IS NOT NULL;", connection, transaction);
                    if (!(bool)(await marker.ExecuteScalarAsync(cancellationToken))!)
                    {
                        // No adoption, reset or migration of existing pre-release installations.
                        return;
                    }

                    installedVersion = await ReadVersionAsync(connection, transaction, cancellationToken);
                    if (installedVersion == 1 + _migrations.Count)
                    {
                        return;
                    }

                    await using var accounts = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM \"Accounts\");", connection, transaction);
                    requiresKeys = (bool)(await accounts.ExecuteScalarAsync(cancellationToken))!;
                    await transaction.CommitAsync(cancellationToken);
                }

                await BackupFiles.CreateAsync(_backup, _tools, DateTime.UtcNow, installedVersion, Fingerprint(installedVersion, _migrations), cancellationToken, requiresKeys);

                await using var migrationTransaction = await connection.BeginTransactionAsync(cancellationToken);
                if (await ReadVersionAsync(connection, migrationTransaction, cancellationToken) != installedVersion)
                {
                    throw new InvalidDataException("The database schema changed during its pre-upgrade backup. No migrations were applied.");
                }

                await ApplyMigrationsAsync(connection, migrationTransaction, installedVersion, cancellationToken);
                await migrationTransaction.CommitAsync(cancellationToken);
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

        private async Task InstallFreshAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
        {
            foreach (var name in Baselines)
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

            await RecordVersionAsync(connection, transaction, 1, ct);
        }

        private async Task ApplyMigrationsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, int installedVersion, CancellationToken ct)
        {
            foreach (var migration in _migrations.Where(migration => migration.Version > installedVersion))
            {
                await using var command = new NpgsqlCommand(migration.Statement, connection, transaction);
                await command.ExecuteNonQueryAsync(ct);
                await RecordVersionAsync(connection, transaction, migration.Version, ct);
            }
        }

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
                    throw new InvalidDataException("The database belongs to a newer Jularr release. Start a compatible image or recover the previous image and backup together.");
                }

                if (next != version + 1 || reader.GetString(1) != Fingerprint(next, _migrations))
                {
                    throw new InvalidDataException("The database schema history does not match a supported release. No schema changes were applied.");
                }

                version = next;
            }

            return version > 0 ? version : throw new InvalidDataException("The database schema history is empty. No schema changes were applied.");
        }

        private async Task RecordVersionAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, int version, CancellationToken ct)
        {
            await using var command = new NpgsqlCommand("INSERT INTO \"DatabaseSchemaVersions\" (\"Version\", \"Fingerprint\") VALUES (@Version, @Fingerprint);", connection, transaction);
            command.Parameters.AddWithValue("Version", version);
            command.Parameters.AddWithValue("Fingerprint", Fingerprint(version, _migrations));
            await command.ExecuteNonQueryAsync(ct);
        }

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

    internal sealed class DatabaseMigration(int version, string statement)
    {
        public int Version { get; } = version;
        public string Statement { get; } = statement;
    }
}
