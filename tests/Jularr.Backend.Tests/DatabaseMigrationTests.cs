using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Jularr.Backend.Service.Core;
using Jularr.Backend.Tests.Infrastructure;
using Jularr.Frontend.Framework;
using Microsoft.Extensions.DependencyInjection;
using static Jularr.Backend.Tests.DatabaseBaselineTests;

namespace Jularr.Backend.Tests
{
    // The migration mechanism after the release freeze: explicit test migrations use a version-1 fixture independently of production migrations,
    // and a script stands in for pg_dump so the backup-first rule is tested without the PostgreSQL client.
    [TestClass]
    public sealed class DatabaseMigrationTests
    {
        private const string AddColumnAndTable =
            """
            ALTER TABLE "Works" ADD COLUMN "UpgradeNote" text NOT NULL DEFAULT 'migrated';
            CREATE TABLE "UpgradeNotes" ("Id" bigint PRIMARY KEY, "WorkId" bigint NOT NULL REFERENCES "Works" ("Id"));
            """;

        // Writes the custom-format signature to the --file target, or fails like a broken pg_dump.
        private static string FakeDump(string folder, bool succeeds)
        {
            if (OperatingSystem.IsWindows())
            {
                var cmd = Path.Combine(folder, "fake-pg-dump.cmd");
                File.WriteAllText(cmd, succeeds
                    ? "@echo off\r\n:loop\r\nif \"%~1\"==\"\" exit /b 1\r\nif \"%~1\"==\"--file\" goto found\r\nshift\r\ngoto loop\r\n:found\r\n<nul set /p \"=PGDMP\" > \"%~2\"\r\nexit /b 0\r\n"
                    : "@echo off\r\necho pg_dump: error: simulated failure 1>&2\r\nexit /b 1\r\n");
                return cmd;
            }

            var script = Path.Combine(folder, "fake-pg-dump.sh");
            File.WriteAllText(script, succeeds
                ? "#!/bin/sh\nfor a in \"$@\"; do case \"$a\" in --file=*) printf 'PGDMP' > \"${a#--file=}\";; esac; done\n"
                : "#!/bin/sh\necho 'pg_dump: error: simulated failure' >&2\nexit 1\n");
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return script;
        }

        private static BackupOptions Backup(string folder, out string backups)
        {
            backups = Directory.CreateDirectory(Path.Combine(folder, "backups")).FullName;
            var keys = Directory.CreateDirectory(Path.Combine(folder, "keys")).FullName;
            File.WriteAllText(Path.Combine(keys, "key-1.xml"), "<key/>");
            return new BackupOptions { Path = backups, KeyDirectory = keys };
        }

        private static Task InsertWorkAsync(string cs) => ExecuteAsync(cs,
            """
            INSERT INTO "Accounts" ("Id", "Username", "Email", "DisplayName", "AccountRoleTypeId") VALUES (41, 'migrate_owner', 'migrate@example.test', 'Migrate Owner', 1);
            INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle") VALUES (501, 1, 'Kept Film');
            """);

        [TestMethod]
        public async Task Upgrade_ProductionFeedMigration_PreservesVersionOneDataAndBacksUpOnce()
        {
            using var folder = new LibraryTests.TemporaryDirectory();
            var cs = TestPostgres.ResolveConnectionString(nameof(Upgrade_ProductionFeedMigration_PreservesVersionOneDataAndBacksUpOnce));
            await new DatabaseBaseline(cs, migrations: []).StartAsync(CancellationToken.None);
            await InsertWorkAsync(cs);
            var options = Backup(folder.Path, out var backups);
            var current = new DatabaseBaseline(cs, options, Tools(cs, FakeDump(folder.Path, true), null));
            await current.StartAsync(CancellationToken.None);
            await current.StartAsync(CancellationToken.None);
            Assert.AreEqual(DatabaseBaseline.CurrentVersion, await ScalarAsync(cs, "SELECT max(\"Version\") FROM \"DatabaseSchemaVersions\""));
            Assert.AreEqual("Kept Film", await ScalarAsync(cs, "SELECT \"CanonicalTitle\" FROM \"Works\" WHERE \"Id\" = 501"));
            Assert.AreEqual(2L, await ScalarAsync(cs, "SELECT count(*) FROM information_schema.tables WHERE table_schema = current_schema() AND table_name IN ('ExternalWorkFeeds', 'ExternalWorkFeedItems')"));
            Assert.AreEqual(1, Directory.GetDirectories(backups).Length);
        }

        [TestMethod]
        public async Task Upgrade_AppliesNumberedMigrations_AfterABackup_KeepsData_AndIsConcurrencySafe()
        {
            using var folder = new LibraryTests.TemporaryDirectory();
            var cs = TestPostgres.ResolveConnectionString(nameof(Upgrade_AppliesNumberedMigrations_AfterABackup_KeepsData_AndIsConcurrencySafe));
            var options = Backup(folder.Path, out var backups);
            var tools = Tools(cs, FakeDump(folder.Path, true), null);

            // Version 1 with data and nothing pending: no backup.
            await new DatabaseBaseline(cs, options, tools, migrations: []).StartAsync(CancellationToken.None);
            await InsertWorkAsync(cs);
            Assert.AreEqual(0, Directory.GetDirectories(backups).Length);

            DatabaseMigration[] migrations = [Probe(2, AddColumnAndTable)];
            await Task.WhenAll(new DatabaseBaseline(cs, options, tools, migrations).StartAsync(CancellationToken.None), new DatabaseBaseline(cs, options, tools, migrations).StartAsync(CancellationToken.None));

            Assert.AreEqual("Kept Film", await ScalarAsync(cs, "SELECT \"CanonicalTitle\" FROM \"Works\" WHERE \"Id\" = 501;"));
            Assert.AreEqual("migrated", await ScalarAsync(cs, "SELECT \"UpgradeNote\" FROM \"Works\" WHERE \"Id\" = 501;"));
            Assert.AreEqual(1L, await ScalarAsync(cs, "SELECT count(*) FROM \"Accounts\";"));
            Assert.AreEqual(1L, await ScalarAsync(cs, "SELECT count(*) FROM pg_tables WHERE tablename = 'UpgradeNotes';"));
            Assert.AreEqual(2, await ScalarAsync(cs, "SELECT max(\"Version\") FROM \"DatabaseSchemaVersions\";"));
            Assert.AreEqual("0002_probe", await ScalarAsync(cs, "SELECT \"Name\" FROM \"DatabaseSchemaVersions\" WHERE \"Version\" = 2;"));
            Assert.AreEqual(DatabaseBaseline.Fingerprint(2, migrations), await ScalarAsync(cs, "SELECT \"Fingerprint\" FROM \"DatabaseSchemaVersions\" WHERE \"Version\" = 2;"));

            // One backup, made before the migration (schema version 1); the second concurrent start found nothing pending.
            var backup = Assert.ContainsSingle(Directory.GetDirectories(backups));
            StringAssert.Contains(await File.ReadAllTextAsync(Path.Combine(backup, "backup.json")), "\"SchemaVersion\":1");
            Assert.IsTrue(File.Exists(Path.Combine(backup, BackupFiles.DumpName)));

            await new DatabaseBaseline(cs, options, tools, migrations).StartAsync(CancellationToken.None);
            Assert.AreEqual(1, Directory.GetDirectories(backups).Length);
        }

        [TestMethod]
        public async Task Upgrade_FailingMigration_RollsEverythingBack_KeepsTheBackup_AndRetries()
        {
            using var folder = new LibraryTests.TemporaryDirectory();
            var cs = TestPostgres.ResolveConnectionString(nameof(Upgrade_FailingMigration_RollsEverythingBack_KeepsTheBackup_AndRetries));
            var options = Backup(folder.Path, out var backups);
            var tools = Tools(cs, FakeDump(folder.Path, true), null);
            await new DatabaseBaseline(cs, options, tools, migrations: []).StartAsync(CancellationToken.None);
            await InsertWorkAsync(cs);

            var broken = new DatabaseBaseline(cs, options, tools, [Probe(2, AddColumnAndTable), Probe(3, "ALTER TABLE \"Works\" ADD COLUMN \"Broken\" int NOT NULL;")]);
            var failure = await Assert.ThrowsExactlyAsync<DatabaseUpgradeException>(() => broken.StartAsync(CancellationToken.None));
            Assert.AreEqual("0003_probe", failure.Migration);

            // Migration 2 ran inside the same transaction, yet none of it remains.
            Assert.AreEqual(1, await ScalarAsync(cs, "SELECT max(\"Version\") FROM \"DatabaseSchemaVersions\";"));
            Assert.AreEqual(0L, await ScalarAsync(cs, "SELECT count(*) FROM pg_tables WHERE tablename = 'UpgradeNotes';"));
            Assert.AreEqual(0L, await ScalarAsync(cs, "SELECT count(*) FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = 'Works' AND column_name = 'UpgradeNote';"));
            Assert.AreEqual("Kept Film", await ScalarAsync(cs, "SELECT \"CanonicalTitle\" FROM \"Works\" WHERE \"Id\" = 501;"));
            Assert.AreEqual(1, Directory.GetDirectories(backups).Length);

            await new DatabaseBaseline(cs, options, tools, [Probe(2, AddColumnAndTable)]).StartAsync(CancellationToken.None);
            Assert.AreEqual(2, await ScalarAsync(cs, "SELECT max(\"Version\") FROM \"DatabaseSchemaVersions\";"));
        }

        [TestMethod]
        public async Task Upgrade_FailedBackup_AbortsBeforeAnyMigration()
        {
            using var folder = new LibraryTests.TemporaryDirectory();
            var cs = TestPostgres.ResolveConnectionString(nameof(Upgrade_FailedBackup_AbortsBeforeAnyMigration));
            var options = Backup(folder.Path, out var backups);
            await new DatabaseBaseline(cs, migrations: []).StartAsync(CancellationToken.None);
            await InsertWorkAsync(cs);

            var failing = new DatabaseBaseline(cs, options, Tools(cs, FakeDump(folder.Path, false), null), [Probe(2, AddColumnAndTable)]);
            var failure = await Assert.ThrowsExactlyAsync<DatabaseUpgradeException>(() => failing.StartAsync(CancellationToken.None));
            Assert.AreEqual("backup", failure.Migration);
            Assert.AreEqual(1, await ScalarAsync(cs, "SELECT max(\"Version\") FROM \"DatabaseSchemaVersions\";"));
            Assert.AreEqual(0L, await ScalarAsync(cs, "SELECT count(*) FROM pg_tables WHERE tablename = 'UpgradeNotes';"));
            Assert.AreEqual(0, Directory.GetFileSystemEntries(backups).Length);
        }

        [TestMethod]
        public async Task Upgrade_ChangedChecksumOfAnAppliedMigration_RefusesToStart()
        {
            using var folder = new LibraryTests.TemporaryDirectory();
            var cs = TestPostgres.ResolveConnectionString(nameof(Upgrade_ChangedChecksumOfAnAppliedMigration_RefusesToStart));
            var options = Backup(folder.Path, out var backups);
            var tools = Tools(cs, FakeDump(folder.Path, true), null);
            await new DatabaseBaseline(cs, options, tools, migrations: []).StartAsync(CancellationToken.None);
            await InsertWorkAsync(cs);
            await new DatabaseBaseline(cs, options, tools, [Probe(2, AddColumnAndTable)]).StartAsync(CancellationToken.None);

            var edited = new DatabaseBaseline(cs, options, tools, [Probe(2, AddColumnAndTable + "\n-- edited after it was applied")]);
            var failure = await Assert.ThrowsExactlyAsync<DatabaseUpgradeException>(() => edited.StartAsync(CancellationToken.None));
            Assert.AreEqual("0002_probe", failure.Migration);
            StringAssert.Contains(failure.Message, "checksum");
            Assert.AreEqual("Kept Film", await ScalarAsync(cs, "SELECT \"CanonicalTitle\" FROM \"Works\" WHERE \"Id\" = 501;"));
            Assert.AreEqual(1, Directory.GetDirectories(backups).Length);
        }

        // A database of the baseline from before the version table existed: complete, so it is marked version 1 and keeps its data; no backup (nothing to migrate).
        [TestMethod]
        public async Task LegacyBaselineDatabase_WithoutVersionTable_IsMarkedVersionOne_WithoutABackup()
        {
            using var folder = new LibraryTests.TemporaryDirectory();
            var cs = TestPostgres.ResolveConnectionString(nameof(LegacyBaselineDatabase_WithoutVersionTable_IsMarkedVersionOne_WithoutABackup));
            await new DatabaseBaseline(cs, migrations: []).StartAsync(CancellationToken.None);
            await ExecuteAsync(cs, "UPDATE \"InstanceSettings\" SET \"Name\" = 'legacy'; DROP TABLE \"DatabaseSchemaVersions\";");

            var legacy = new DatabaseBaseline(cs, new BackupOptions { Path = folder.Path }, migrations: []);
            await legacy.StartAsync(CancellationToken.None);
            Assert.AreEqual("legacy", await ScalarAsync(cs, "SELECT \"Name\" FROM \"InstanceSettings\";"));
            Assert.AreEqual(1, await ScalarAsync(cs, "SELECT max(\"Version\") FROM \"DatabaseSchemaVersions\";"));
            Assert.AreEqual("baseline", await ScalarAsync(cs, "SELECT \"Name\" FROM \"DatabaseSchemaVersions\" WHERE \"Version\" = 1;"));
            Assert.AreEqual(0, Directory.GetFileSystemEntries(folder.Path).Length);
            Assert.AreEqual(0L, await ScalarAsync(cs, "SELECT count(*) FROM pg_namespace WHERE nspname = 'jularr_baseline_check';"));

            await legacy.StartAsync(CancellationToken.None);
            Assert.AreEqual(1L, await ScalarAsync(cs, "SELECT count(*) FROM \"DatabaseSchemaVersions\";"));
        }

        [TestMethod]
        public async Task LegacyBaselineDatabase_WithAMissingColumn_IsRefusedAndNothingIsRecorded()
        {
            var cs = TestPostgres.ResolveConnectionString(nameof(LegacyBaselineDatabase_WithAMissingColumn_IsRefusedAndNothingIsRecorded));
            await new DatabaseBaseline(cs).StartAsync(CancellationToken.None);
            await ExecuteAsync(cs, "DROP TABLE \"DatabaseSchemaVersions\"; ALTER TABLE \"InstanceSettings\" DROP COLUMN \"Name\" CASCADE;");
            var error = await Assert.ThrowsExactlyAsync<DatabaseUpgradeException>(() => new DatabaseBaseline(cs).StartAsync(CancellationToken.None));
            StringAssert.Contains(error.Message, "InstanceSettings.Name");
            Assert.AreEqual(0L, await ScalarAsync(cs, "SELECT count(*) FROM pg_tables WHERE tablename = 'DatabaseSchemaVersions';"));
        }

        [TestMethod]
        public async Task LegacyBaselineDatabase_WithAPendingMigration_BacksUpThenMarksVersionOneAndMigrates()
        {
            using var folder = new LibraryTests.TemporaryDirectory();
            var cs = TestPostgres.ResolveConnectionString(nameof(LegacyBaselineDatabase_WithAPendingMigration_BacksUpThenMarksVersionOneAndMigrates));
            var options = Backup(folder.Path, out var backups);
            await new DatabaseBaseline(cs, migrations: []).StartAsync(CancellationToken.None);
            await InsertWorkAsync(cs);
            await ExecuteAsync(cs, "DROP TABLE \"DatabaseSchemaVersions\";");

            await new DatabaseBaseline(cs, options, Tools(cs, FakeDump(folder.Path, true), null), [Probe(2, AddColumnAndTable)]).StartAsync(CancellationToken.None);
            Assert.AreEqual(2L, await ScalarAsync(cs, "SELECT count(*) FROM \"DatabaseSchemaVersions\";"));
            Assert.AreEqual("migrated", await ScalarAsync(cs, "SELECT \"UpgradeNote\" FROM \"Works\" WHERE \"Id\" = 501;"));
            Assert.AreEqual(1, Directory.GetDirectories(backups).Length);
        }

        [TestMethod]
        public async Task DatabaseUpgrade_FailedUpdate_IsReturnedForTheMaintenancePage_AndACurrentDatabaseReturnsNull()
        {
            var cs = TestPostgres.ResolveConnectionString(nameof(DatabaseUpgrade_FailedUpdate_IsReturnedForTheMaintenancePage_AndACurrentDatabaseReturnsNull));
            await new DatabaseBaseline(cs, migrations: []).StartAsync(CancellationToken.None);

            // Without a backup tool the pending migration cannot start.
            using var failing = new ServiceCollection().AddLogging()
                .AddSingleton(new DatabaseBaseline(cs, new BackupOptions(), null, [Probe(2, "SELECT 1;")])).BuildServiceProvider();
            var failure = await DatabaseUpgrade.RunAsync(failing);
            Assert.AreEqual("backup", failure!.Migration);
            Assert.AreEqual(1, await ScalarAsync(cs, "SELECT max(\"Version\") FROM \"DatabaseSchemaVersions\";"));

            using var current = new ServiceCollection().AddLogging().AddSingleton(new DatabaseBaseline(cs, migrations: [])).BuildServiceProvider();
            Assert.IsNull(await DatabaseUpgrade.RunAsync(current));
        }

        // The maintenance page names the migration in German, never the reason (SQL, paths) that only the log holds.
        [TestMethod]
        public void MaintenancePage_NamesTheMigration_InGerman_WithoutTheReason()
        {
            var page = DatabaseMaintenance.Page(new DatabaseUpgradeFailure("0002_<script>", "relation \"Works\" secret detail"));
            StringAssert.Contains(page, "Datenbank-Update fehlgeschlagen");
            StringAssert.Contains(page, "vorherige Version kann wieder gestartet werden");
            StringAssert.Contains(page, "0002_&lt;script&gt;");
            Assert.DoesNotContain("secret detail", page);
            Assert.DoesNotContain("<script>", page);
            Assert.DoesNotContain("style=", page);
        }

        [TestMethod]
        public async Task MaintenanceHost_AnswersEveryRequestWith503AndThePage()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            using var stop = new CancellationTokenSource();
            var host = DatabaseMaintenance.RunAsync(["--urls", "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture)], new DatabaseUpgradeFailure("0002_probe", "reason"), stop.Token);
            try
            {
                using var client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture)) };
                HttpResponseMessage? response = null;
                for (var attempt = 0; attempt < 50 && response is null; attempt++)
                {
                    try
                    {
                        response = await client.GetAsync("/Admin/Works");
                    }
                    catch (HttpRequestException)
                    {
                        await Task.Delay(100);
                    }
                }

                Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response!.StatusCode);
                StringAssert.Contains(await response.Content.ReadAsStringAsync(), "0002_probe");
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable, (await client.PostAsync("/api/anything", new StringContent(""))).StatusCode);
            }
            finally
            {
                await stop.CancelAsync();
                await host;
            }
        }

        // The frozen baseline (schema version 1): after the release freeze a changed database/*.sql fails here; schema changes are new migrations.
        [TestMethod]
        public void Baseline_MatchesTheFrozenHash()
        {
            var database = Path.Combine(RepositoryRoot(), "database");
            var content = new StringBuilder();
            foreach (var name in DatabaseBaseline.Baselines)
            {
                content.Append(name).Append('\n').Append(File.ReadAllText(Path.Combine(database, name + ".sql")).Replace("\r\n", "\n", StringComparison.Ordinal));
            }

            var actual = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content.ToString())));
            var frozen = File.ReadAllText(Path.Combine(database, "baseline.sha256")).Trim();
            Assert.AreEqual(frozen, actual, "The baseline database/*.sql changed after the release freeze. Add a numbered migration database/migrations/NNNN_short_name.sql instead (docs/DATABASE_MIGRATIONS.md); node scripts/freeze-baseline.mjs is for releases only.");
            Assert.AreEqual(frozen, DatabaseBaseline.Fingerprint(1, []), "The embedded baselines differ from database/*.sql.");

            // Every baseline file is part of the hash.
            CollectionAssert.AreEquivalent(DatabaseBaseline.Baselines.Select(name => name + ".sql").ToArray(), Directory.GetFiles(database, "*.sql").Select(file => Path.GetFileName(file)).ToArray());
        }

        // A migration file that is not embedded (or misnamed) would silently never run.
        [TestMethod]
        public void Migrations_AreEmbeddedFromTheMigrationsFolder_ContiguousFromTwo()
        {
            var folder = Path.Combine(RepositoryRoot(), "database", "migrations");
            var files = Directory.Exists(folder) ? Directory.GetFiles(folder, "*.sql").Select(file => Path.GetFileNameWithoutExtension(file)).Order(StringComparer.Ordinal).ToArray() : [];
            CollectionAssert.AreEqual(files, DatabaseBaseline.Migrations.Select(migration => migration.Name).ToArray());
            for (var index = 0; index < files.Length; index++)
            {
                StringAssert.StartsWith(files[index], (index + 2).ToString("0000", CultureInfo.InvariantCulture) + "_");
            }
        }

        private static string RepositoryRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Jularr.sln")))
            {
                directory = directory.Parent;
            }

            return directory?.FullName ?? throw new DirectoryNotFoundException("The repository root was not found.");
        }
    }
}
