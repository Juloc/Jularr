using Jularr.Backend.Service.Core;
using Jularr.Backend.Tests.Infrastructure;
using Jularr.Integrations.Data.Database;
using Jularr.Integrations.Logic.Common;
using Jularr.Integrations.Logic.Database;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Npgsql;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class DatabaseBaselineTests
    {
        // Fresh installations are versioned; repeated starts keep their data.
        [TestMethod]
        public async Task EmptyDatabase_GetsEveryBaselineOnce_AndAnExistingOneStaysAsItIs()
        {
            var connectionString = TestPostgres.ResolveConnectionString(nameof(DatabaseBaselineTests));
            var baseline = new DatabaseBaseline(connectionString);
            await baseline.StartAsync(CancellationToken.None);
            var tables = await TablesAsync(connectionString);
            CollectionAssert.IsSubsetOf(new[] { "Accounts", "InstanceSettings", "Works", "WantedItems", "StorageMounts", "StoredFiles", "ReadingPreferences", "ReadingRecognitions" }, tables);

            await using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.OpenAsync();
                await using var rename = new NpgsqlCommand("UPDATE \"InstanceSettings\" SET \"Name\" = 'Kept'", connection);
                await rename.ExecuteNonQueryAsync();
            }

            await baseline.StartAsync(CancellationToken.None);
            CollectionAssert.AreEquivalent(tables, await TablesAsync(connectionString));
            await using var check = new NpgsqlConnection(connectionString);
            await check.OpenAsync();
            await using var name = new NpgsqlCommand("SELECT \"Name\" FROM \"InstanceSettings\"", check);
            Assert.AreEqual("Kept", await name.ExecuteScalarAsync());
        }


        [TestMethod]
        public async Task ExternalWorks_FreshInstallCreatesTheProviderCatalog()
        {
            var cs = TestPostgres.ResolveConnectionString(nameof(ExternalWorks_FreshInstallCreatesTheProviderCatalog));
            await new DatabaseBaseline(cs).StartAsync(CancellationToken.None);
            Assert.AreEqual(DatabaseBaseline.CurrentVersion, await ScalarAsync(cs, "SELECT max(\"Version\") FROM \"DatabaseSchemaVersions\";"));
            await ExecuteAsync(cs,
                """
                INSERT INTO "ExternalWorks" ("Namespace", "Value", "MediaTypeId", "Title", "OriginalTitle", "ImdbId", "Rating", "TrendingRank")
                VALUES ('tmdb_movie', '603', 1, 'Matrix', 'The Matrix', 'tt0133093', 8.2, 1);
                """);
            Assert.AreEqual(1L, await ScalarAsync(cs, "SELECT count(*) FROM \"ExternalWorks\" WHERE \"SearchText\" @@ to_tsquery('simple', 'matrix');"));
            // One row per provider title, and only catalogs Jularr reads.
            await Assert.ThrowsExactlyAsync<PostgresException>(() => ExecuteAsync(cs,
                """
                INSERT INTO "ExternalWorks" ("Namespace", "Value", "MediaTypeId", "Title") VALUES ('tmdb_movie', '603', 1, 'Again');
                """));
            await Assert.ThrowsExactlyAsync<PostgresException>(() => ExecuteAsync(cs,
                """
                INSERT INTO "ExternalWorks" ("Namespace", "Value", "MediaTypeId", "Title") VALUES ('plex', '1', 1, 'Unknown catalog');
                """));
            await new DatabaseBaseline(cs).StartAsync(CancellationToken.None);
            Assert.AreEqual(1L, await ScalarAsync(cs, "SELECT count(*) FROM \"ExternalWorks\";"));
        }

        [TestMethod]
        public async Task LegacyDatabase_NotMatchingTheBaseline_IsRefusedAndStaysUnchanged()
        {
            var cs = TestPostgres.ResolveConnectionString(nameof(LegacyDatabase_NotMatchingTheBaseline_IsRefusedAndStaysUnchanged));
            await ExecuteAsync(cs, "CREATE TABLE \"Unversioned\" (\"Value\" text); INSERT INTO \"Unversioned\" VALUES ('kept');");
            var error = await Assert.ThrowsExactlyAsync<DatabaseUpgradeException>(() => new DatabaseBaseline(cs).StartAsync(CancellationToken.None));
            Assert.AreEqual("baseline", error.Migration);
            CollectionAssert.AreEqual(new[] { "Unversioned" }, await TablesAsync(cs));
            Assert.AreEqual("kept", await ScalarAsync(cs, "SELECT \"Value\" FROM \"Unversioned\";"));
        }

        [TestMethod]
        public async Task ConcurrentFreshStarts_InstallOneCompleteHistory()
        {
            var cs = TestPostgres.ResolveConnectionString(nameof(ConcurrentFreshStarts_InstallOneCompleteHistory));
            await Task.WhenAll(new DatabaseBaseline(cs).StartAsync(CancellationToken.None), new DatabaseBaseline(cs).StartAsync(CancellationToken.None));
            Assert.AreEqual((long)DatabaseBaseline.CurrentVersion, await ScalarAsync(cs, "SELECT count(*) FROM \"DatabaseSchemaVersions\";"));
            Assert.AreEqual(DatabaseBaseline.CurrentFingerprint, await ScalarAsync(cs, "SELECT \"Fingerprint\" FROM \"DatabaseSchemaVersions\" ORDER BY \"Version\" DESC LIMIT 1;"));
        }

        [TestMethod]
        [DataRow("INSERT INTO \"DatabaseSchemaVersions\" (\"Version\", \"Name\", \"Fingerprint\") VALUES (99, 'future', repeat('a', 64));")]
        [DataRow("UPDATE \"DatabaseSchemaVersions\" SET \"Fingerprint\" = repeat('0', 64);")]
        [DataRow("DELETE FROM \"DatabaseSchemaVersions\";")]
        public async Task UnsupportedVersionHistory_StopsWithoutChangingData(string change)
        {
            var cs = TestPostgres.ResolveConnectionString(Guid.NewGuid().ToString());
            var baseline = new DatabaseBaseline(cs);
            await baseline.StartAsync(CancellationToken.None);
            await ExecuteAsync(cs, "UPDATE \"InstanceSettings\" SET \"Name\" = 'preserved'; " + change);
            await Assert.ThrowsExactlyAsync<DatabaseUpgradeException>(() => baseline.StartAsync(CancellationToken.None));
            Assert.AreEqual("preserved", await ScalarAsync(cs, "SELECT \"Name\" FROM \"InstanceSettings\";"));
        }

        [TestMethod]
        public async Task Upgrade_MissingBackupTool_LeavesSchemaVersionAndDataUnchanged()
        {
            using var folder = new LibraryTests.TemporaryDirectory();
            var cs = TestPostgres.ResolveConnectionString(nameof(Upgrade_MissingBackupTool_LeavesSchemaVersionAndDataUnchanged));
            await new DatabaseBaseline(cs).StartAsync(CancellationToken.None);
            await ExecuteAsync(cs, "UPDATE \"InstanceSettings\" SET \"Name\" = 'preserved';");
            var tools = Tools(cs, Path.Combine(folder.Path, "missing-pg-dump"), null);
            var next = new DatabaseBaseline(cs, new BackupOptions { Path = folder.Path }, tools,
                [.. DatabaseBaseline.Migrations, Probe(DatabaseBaseline.CurrentVersion + 1, "ALTER TABLE \"Accounts\" ADD COLUMN \"UpgradeProbe\" text;")]);
            var failure = await Assert.ThrowsExactlyAsync<DatabaseUpgradeException>(() => next.StartAsync(CancellationToken.None));
            Assert.AreEqual("backup", failure.Migration);
            Assert.AreEqual("backup_tool_missing", ((PostgresToolException)failure.InnerException!).Code);
            Assert.AreEqual(DatabaseBaseline.CurrentVersion, await ScalarAsync(cs, "SELECT max(\"Version\") FROM \"DatabaseSchemaVersions\";"));
            Assert.AreEqual(0L, await ScalarAsync(cs, "SELECT count(*) FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = 'Accounts' AND column_name = 'UpgradeProbe';"));
            Assert.AreEqual("preserved", await ScalarAsync(cs, "SELECT \"Name\" FROM \"InstanceSettings\";"));
            Assert.AreEqual(0, Directory.GetFileSystemEntries(folder.Path).Length);
        }

        [TestMethod]
        public async Task Upgrade_ExistingAccountsAndMissingKeyRing_AbortsBeforeSchemaChanges()
        {
            using var folder = new LibraryTests.TemporaryDirectory();
            var cs = TestPostgres.ResolveConnectionString(nameof(Upgrade_ExistingAccountsAndMissingKeyRing_AbortsBeforeSchemaChanges));
            await new DatabaseBaseline(cs).StartAsync(CancellationToken.None);
            await ExecuteAsync(cs, "INSERT INTO \"Accounts\" (\"Id\", \"Username\", \"Email\", \"DisplayName\", \"AccountRoleTypeId\") VALUES (41, 'keys_owner', 'keys@example.test', 'Keys Owner', 1);");
            var options = new BackupOptions { Path = folder.Path, KeyDirectory = Path.Combine(folder.Path, "missing-keys") };
            var next = new DatabaseBaseline(cs, options, migrations: [.. DatabaseBaseline.Migrations, Probe(DatabaseBaseline.CurrentVersion + 1, "ALTER TABLE \"Accounts\" ADD COLUMN \"UpgradeProbe\" text;")]);
            var error = await Assert.ThrowsExactlyAsync<DatabaseUpgradeException>(() => next.StartAsync(CancellationToken.None));
            Assert.AreEqual("backup", error.Migration);
            StringAssert.Contains(error.Message, "key ring");
            Assert.AreEqual(DatabaseBaseline.CurrentVersion, await ScalarAsync(cs, "SELECT max(\"Version\") FROM \"DatabaseSchemaVersions\";"));
            Assert.AreEqual(0L, await ScalarAsync(cs, "SELECT count(*) FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = 'Accounts' AND column_name = 'UpgradeProbe';"));
            Assert.AreEqual(0, Directory.GetFileSystemEntries(folder.Path).Length);
        }

        [TestMethod]
        public async Task FreshInstall_FailedMigration_RollsBackTheEntireInstallation()
        {
            var cs = TestPostgres.ResolveConnectionString(nameof(FreshInstall_FailedMigration_RollsBackTheEntireInstallation));
            var broken = new DatabaseBaseline(cs, migrations:
                [.. DatabaseBaseline.Migrations, Probe(DatabaseBaseline.CurrentVersion + 1, "CREATE TABLE \"UpgradeProbe\" (\"Id\" bigint);"), Probe(DatabaseBaseline.CurrentVersion + 2, "SELECT 1 / 0;")]);
            var failed = await Assert.ThrowsExactlyAsync<DatabaseUpgradeException>(() => broken.StartAsync(CancellationToken.None));
            Assert.AreEqual($"{DatabaseBaseline.CurrentVersion + 2:D4}_probe", failed.Migration);
            Assert.AreEqual(0, (await TablesAsync(cs)).Length);
            await new DatabaseBaseline(cs).StartAsync(CancellationToken.None);
            Assert.AreEqual(DatabaseBaseline.CurrentVersion, await ScalarAsync(cs, "SELECT max(\"Version\") FROM \"DatabaseSchemaVersions\";"));
        }

        [TestMethod]
        public async Task ReleaseUpgrade_BackupRestoreRetryAndConcurrentStarts_PreserveDataAndKeys()
        {
            var dump = Executables.Resolve(Environment.GetEnvironmentVariable("JULARR_TEST_PG_DUMP"), "pg_dump");
            var restore = Executables.Resolve(Environment.GetEnvironmentVariable("JULARR_TEST_PG_RESTORE"), "pg_restore");
            if (dump is null || restore is null)
            {
                Assert.Inconclusive("The real PostgreSQL 18 dump/restore tools are required for release upgrade acceptance.");
            }

            using var backups = new LibraryTests.TemporaryDirectory();
            using var keys = new LibraryTests.TemporaryDirectory();
            var cs = TestPostgres.ResolveConnectionString(nameof(ReleaseUpgrade_BackupRestoreRetryAndConcurrentStarts_PreserveDataAndKeys));
            await new DatabaseBaseline(cs).StartAsync(CancellationToken.None);
            await ExecuteAsync(cs,
                """
                BEGIN;
                UPDATE "InstanceSettings" SET "Name" = 'Release N';
                INSERT INTO "Accounts" ("Id", "Username", "Email", "DisplayName", "AccountRoleTypeId") VALUES (41, 'upgrade_owner', 'upgrade@example.test', 'Upgrade Owner', 1);
                INSERT INTO "AccountPasswords" ("AccountId", "PasswordHash") VALUES (41, 'isolated-opaque-password-hash');
                INSERT INTO "Profiles" ("Id", "OwnerAccountId", "DisplayName", "UiLocaleId") VALUES (101, 41, 'Upgrade Profile', 1);
                INSERT INTO "AccountProfiles" ("AccountId", "ProfileId") VALUES (41, 101);
                INSERT INTO "AccountSessions" ("PublicId", "AccountId", "ActiveProfileId", "TokenHash", "ExpiresAt")
                VALUES ('66dfbf7e-01b6-49e8-b33f-013005305503', 41, 101, decode(repeat('01', 32), 'hex'), now() + interval '1 day');
                INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle") VALUES (501, 1, 'Upgrade Film');
                INSERT INTO "WantedItems" ("Id", "WorkId", "IsPaused", "MonitorsNewUnits") VALUES (601, 501, TRUE, FALSE);
                INSERT INTO "AcquisitionRequests" ("Id", "WorkId", "RequestedByProfileId", "RequestedByAccountId", "UiLocaleId") VALUES (701, 501, 101, 41, 1);
                INSERT INTO "AcquisitionConnections" ("Id", "Name", "AcquisitionConnectionTypeId", "BaseUrl", "ProtectedApiKey")
                VALUES (801, 'Upgrade SAB', 2, 'http://sab.example.test', decode('01020304', 'hex'));
                INSERT INTO "AcquisitionReleases" ("Id", "WantedItemId", "AcquisitionConnectionId", "ReleaseIdentity", "Title", "AcquisitionReleaseStatusTypeId")
                VALUES (901, 601, 801, 'upgrade-release', 'Upgrade Film Release', 1);
                INSERT INTO "Operations" ("Id", "OperationTypeId", "AcquisitionReleaseId", "AcquisitionConnectionId", "RequestedByAccountId", "ProgressPercent", "SizeBytes")
                VALUES (1001, 2, 901, 801, 41, 35, 1024000);
                INSERT INTO "MediaProgress" ("ProfileId", "WorkId", "ProgressPositionTypeId", "PositionMs", "DurationMs") VALUES (101, 501, 1, 45000, 90000);
                COMMIT;
                """);
            var protection = DataProtectionProvider.Create(new DirectoryInfo(keys.Path), builder => builder.SetApplicationName("Jularr"));
            var plaintext = "isolated-upgrade-secret"u8.ToArray();
            var protectedValue = protection.CreateProtector("release-upgrade-test").Protect(plaintext);
            var passwords = new PasswordHasher<object>();
            await using (var connection = new NpgsqlConnection(cs))
            {
                await connection.OpenAsync();
                await using var credentials = new NpgsqlCommand("UPDATE \"AcquisitionConnections\" SET \"ProtectedApiKey\" = @Secret WHERE \"Id\" = 801; UPDATE \"AccountPasswords\" SET \"PasswordHash\" = @Hash WHERE \"AccountId\" = 41;", connection);
                credentials.Parameters.AddWithValue("Secret", protectedValue);
                credentials.Parameters.AddWithValue("Hash", passwords.HashPassword(new object(), "isolated-upgrade-password"));
                await credentials.ExecuteNonQueryAsync();
            }

            var before = await ReleaseDataAsync(cs);
            var options = new BackupOptions { Path = backups.Path, KeyDirectory = keys.Path, Passphrase = "isolated-upgrade-test" };
            var tools = Tools(cs, dump, restore);
            DatabaseMigration[] migrations = [.. DatabaseBaseline.Migrations, Probe(DatabaseBaseline.CurrentVersion + 1, "ALTER TABLE \"InstanceSettings\" ADD COLUMN \"UpgradeProbe\" text NOT NULL DEFAULT 'N+1';")];
            var broken = new DatabaseBaseline(cs, options, tools, [.. migrations, Probe(DatabaseBaseline.CurrentVersion + 2, "SELECT 1 / 0;")]);
            var failedUpgrade = await Assert.ThrowsExactlyAsync<DatabaseUpgradeException>(() => broken.StartAsync(CancellationToken.None));
            Assert.IsInstanceOfType<PostgresException>(failedUpgrade.InnerException);
            Assert.AreEqual(DatabaseBaseline.CurrentVersion, await ScalarAsync(cs, "SELECT max(\"Version\") FROM \"DatabaseSchemaVersions\";"));
            Assert.AreEqual(0L, await ScalarAsync(cs, "SELECT count(*) FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = 'InstanceSettings' AND column_name = 'UpgradeProbe';"));
            Assert.AreEqual(before, await ReleaseDataAsync(cs));
            var next = new DatabaseBaseline(cs, options, tools, migrations);
            await Task.WhenAll(next.StartAsync(CancellationToken.None), next.StartAsync(CancellationToken.None));
            await next.StartAsync(CancellationToken.None);
            Assert.AreEqual("Release N", await ScalarAsync(cs, "SELECT \"Name\" FROM \"InstanceSettings\";"));
            Assert.AreEqual("N+1", await ScalarAsync(cs, "SELECT \"UpgradeProbe\" FROM \"InstanceSettings\";"));
            Assert.AreEqual(before, await ReleaseDataAsync(cs));
            Assert.AreEqual((long)DatabaseBaseline.CurrentVersion + 1, await ScalarAsync(cs, "SELECT count(*) FROM \"DatabaseSchemaVersions\";"));
            Assert.AreEqual(2, Directory.GetDirectories(backups.Path).Length);
            var backup = Directory.GetDirectories(backups.Path).Order(StringComparer.Ordinal).First();
            StringAssert.Contains(await File.ReadAllTextAsync(Path.Combine(backup, "backup.json")), "\"SchemaVersion\":" + DatabaseBaseline.CurrentVersion);
            Assert.IsTrue(File.Exists(Path.Combine(backup, BackupFiles.SealedKeysName)));

            // Restore while the application is stopped, then use the same upgrade owner.
            var restored = TestPostgres.ResolveConnectionString(nameof(ReleaseUpgrade_BackupRestoreRetryAndConcurrentStarts_PreserveDataAndKeys) + "-restored");
            var restoreTools = Tools(restored, dump, restore);
            await restoreTools.RestoreAsync(Path.Combine(backup, BackupFiles.DumpName), CancellationToken.None);
            Assert.AreEqual(DatabaseBaseline.CurrentVersion, await ScalarAsync(restored, "SELECT max(\"Version\") FROM \"DatabaseSchemaVersions\";"));
            using var recoveredKeys = new LibraryTests.TemporaryDirectory();
            BackupFiles.UnsealKeys(Path.Combine(backup, BackupFiles.SealedKeysName), recoveredKeys.Path, options.Passphrase);
            foreach (var key in Directory.EnumerateFiles(keys.Path, "*.xml"))
            {
                Assert.AreEqual(await File.ReadAllTextAsync(key), await File.ReadAllTextAsync(Path.Combine(recoveredKeys.Path, Path.GetFileName(key))));
            }

            using var recoveredBackups = new LibraryTests.TemporaryDirectory();
            await new DatabaseBaseline(restored, new BackupOptions { Path = recoveredBackups.Path, KeyDirectory = recoveredKeys.Path }, restoreTools, migrations).StartAsync(CancellationToken.None);
            Assert.AreEqual("Release N", await ScalarAsync(restored, "SELECT \"Name\" FROM \"InstanceSettings\";"));
            Assert.AreEqual("N+1", await ScalarAsync(restored, "SELECT \"UpgradeProbe\" FROM \"InstanceSettings\";"));
            Assert.AreEqual(before, await ReleaseDataAsync(restored));
            var recoveredProtection = DataProtectionProvider.Create(new DirectoryInfo(recoveredKeys.Path), builder => builder.SetApplicationName("Jularr"));
            CollectionAssert.AreEqual(plaintext, recoveredProtection.CreateProtector("release-upgrade-test").Unprotect((byte[])(await ScalarAsync(restored, "SELECT \"ProtectedApiKey\" FROM \"AcquisitionConnections\" WHERE \"Id\" = 801;"))!));
            Assert.AreEqual(PasswordVerificationResult.Success, passwords.VerifyHashedPassword(new object(), (string)(await ScalarAsync(restored, "SELECT \"PasswordHash\" FROM \"AccountPasswords\" WHERE \"AccountId\" = 41;"))!, "isolated-upgrade-password"));
        }

        // A migration of the test assembly only: the production list has none until the first schema change after the release.
        internal static DatabaseMigration Probe(int version, string statement) =>
            new(version, version.ToString("0000", System.Globalization.CultureInfo.InvariantCulture) + "_probe", statement);

        private static async Task<string> ReleaseDataAsync(string cs)
        {
            var values = new List<string>();
            foreach (var table in new[] { "Accounts", "AccountPasswords", "Profiles", "AccountProfiles", "AccountSessions", "Works", "WantedItems", "AcquisitionRequests", "AcquisitionConnections", "AcquisitionReleases", "Operations", "MediaProgress" })
            {
                values.Add((string)(await ScalarAsync(cs, $"SELECT jsonb_agg(to_jsonb(row) ORDER BY to_jsonb(row)::text)::text FROM \"{table}\" AS row;"))!);
            }

            return string.Join('\n', values);
        }

        internal static PostgresBackupTools Tools(string cs, string? dump, string? restore)
        {
            var target = new NpgsqlConnectionStringBuilder(cs);
            return new PostgresBackupTools
            {
                PgDumpPath = dump,
                PgRestorePath = restore,
                Connection = new PostgresConnection(target.Host!, target.Port, target.Database!, target.Username!, target.Password)
            };
        }

        internal static async Task ExecuteAsync(string cs, string statement)
        {
            await using var connection = new NpgsqlConnection(cs);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(statement, connection);
            await command.ExecuteNonQueryAsync();
        }

        internal static async Task<object?> ScalarAsync(string cs, string statement)
        {
            await using var connection = new NpgsqlConnection(cs);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(statement, connection);
            return await command.ExecuteScalarAsync();
        }

        // Each file is one transaction of its own when applied alone; nothing may follow its COMMIT.
        [TestMethod]
        public void Baselines_AreEachExactlyOneTransactionBlock()
        {
            foreach (var baseline in DatabaseBaseline.Baselines)
            {
                using var reader = new StreamReader(typeof(DatabaseBaseline).Assembly.GetManifestResourceStream("Jularr.Database." + baseline + ".sql")!);
                var text = reader.ReadToEnd().Trim();
                Assert.IsTrue(text.StartsWith("BEGIN;", StringComparison.Ordinal), baseline);
                Assert.IsTrue(text.EndsWith("COMMIT;", StringComparison.Ordinal), baseline);
                Assert.AreEqual(2, text.Split("COMMIT;").Length, baseline);
            }
        }

        internal static async Task<string[]> TablesAsync(string connectionString)
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("SELECT array_agg(tablename::text ORDER BY tablename) FROM pg_tables WHERE schemaname = current_schema()", connection);
            return await command.ExecuteScalarAsync() is string[] tables ? tables : [];
        }
    }
}
