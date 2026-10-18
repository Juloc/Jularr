using Jularr.Backend.Service.Core;
using Jularr.Backend.Tests.Infrastructure;
using Npgsql;
using Fixture = Jularr.Backend.Tests.ProfileReferenceVerticalTests.ProfileFixture;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class DatabaseBaselineTests
    {
        // A minimal Compose starts on an empty database; a database with tables is never touched again.
        [TestMethod]
        public async Task EmptyDatabase_GetsEveryBaselineOnce_AndAnExistingOneStaysAsItIs()
        {
            var connectionString = TestPostgres.ResolveConnectionString(nameof(DatabaseBaselineTests));
            var baseline = new DatabaseBaseline(connectionString);
            await baseline.StartAsync(CancellationToken.None);
            var tables = await TablesAsync(connectionString);
            CollectionAssert.IsSubsetOf(new[] { "Accounts", "InstanceSettings", "Works", "WantedItems", "StorageMounts", "StoredFiles", "ReadingLanguages", "ReadingRecognitions", "DiscoverySnapshots", "SchemaMigrations" }, tables);

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

        [TestMethod]
        public async Task DiscoverySnapshots_UpgradeAndRollback_KeepExistingAccountData()
        {
            await using var fixture = await Fixture.CreateAsync();
            var up = await MigrationSqlAsync("up");
            var down = await MigrationSqlAsync("down");

            // A pre-existing installation has account data but not this new cache table.
            Assert.AreEqual(2L, await ScalarLongAsync(fixture, "SELECT count(*) FROM \"Accounts\""));
            await fixture.RunSqlAsync(up);
            Assert.AreEqual(1L, await ScalarLongAsync(fixture,
                "SELECT count(*) FROM \"SchemaMigrations\" WHERE \"Key\" = '20261018_01_DiscoverySnapshots'"));

            await fixture.RunSqlAsync(
                """
                INSERT INTO "DiscoverySnapshots" ("Key", "ProviderKey", "Payload", "FetchedAt", "FreshUntil", "RetainUntil")
                VALUES ('tmdb.movies.trending.en', 'tmdb', '[{"id":603}]', now(), now() + interval '1 hour', now() + interval '150 days');
                """);
            Assert.AreEqual(1L, await ScalarLongAsync(fixture, "SELECT count(*) FROM \"DiscoverySnapshots\""));

            // A repeated upgrade fails atomically and cannot discard the stored payload.
            await Assert.ThrowsExactlyAsync<PostgresException>(() => fixture.RunSqlAsync(up));
            Assert.AreEqual(1L, await ScalarLongAsync(fixture, "SELECT count(*) FROM \"DiscoverySnapshots\""));
            Assert.AreEqual(2L, await ScalarLongAsync(fixture, "SELECT count(*) FROM \"Accounts\""));

            await fixture.RunSqlAsync(down);
            Assert.AreEqual(0L, await ScalarLongAsync(fixture,
                "SELECT count(*) FROM pg_catalog.pg_tables WHERE schemaname = current_schema() AND tablename = 'DiscoverySnapshots'"));
            Assert.AreEqual(0L, await ScalarLongAsync(fixture, "SELECT count(*) FROM \"SchemaMigrations\""));
            Assert.AreEqual(2L, await ScalarLongAsync(fixture, "SELECT count(*) FROM \"Accounts\""));

            // An administrator may reapply the same release after a clean rollback.
            await fixture.RunSqlAsync(up);
            Assert.AreEqual(1L, await ScalarLongAsync(fixture,
                "SELECT count(*) FROM pg_catalog.pg_tables WHERE schemaname = current_schema() AND tablename = 'DiscoverySnapshots'"));
        }

        [TestMethod]
        public async Task DiscoverySnapshots_UpgradeFailure_IsAtomicAndDoesNotOverwriteAnExistingTable()
        {
            await using var fixture = await Fixture.CreateAsync();
            await fixture.RunSqlAsync("CREATE TABLE \"DiscoverySnapshots\" (\"Key\" integer PRIMARY KEY);");

            await Assert.ThrowsExactlyAsync<PostgresException>(() => fixture.RunSqlAsync(await MigrationSqlAsync("up")));
            Assert.AreEqual(0L, await ScalarLongAsync(fixture,
                "SELECT count(*) FROM pg_catalog.pg_tables WHERE schemaname = current_schema() AND tablename = 'SchemaMigrations'"));
            Assert.AreEqual(1L, await ScalarLongAsync(fixture,
                "SELECT count(*) FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = 'DiscoverySnapshots'"));
            Assert.AreEqual(2L, await ScalarLongAsync(fixture, "SELECT count(*) FROM \"Accounts\""));
        }

        private static async Task<string> MigrationSqlAsync(string direction)
        {
            var name = "Jularr.Database.Migrations.20261018_01_DiscoverySnapshots." + direction + ".sql";
            using var stream = typeof(DatabaseBaseline).Assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException("Missing embedded schema migration: " + name);
            using var reader = new StreamReader(stream);
            var sql = await reader.ReadToEndAsync();
            Assert.IsTrue(sql.StartsWith("BEGIN;", StringComparison.Ordinal), direction);
            Assert.IsTrue(sql.TrimEnd().EndsWith("COMMIT;", StringComparison.Ordinal), direction);
            return sql;
        }

        private static async Task<long> ScalarLongAsync(Fixture fixture, string sql)
        {
            await using var connection = await fixture.Database.OpenConnectionAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            return Convert.ToInt64(await command.ExecuteScalarAsync());
        }

        private static async Task<string[]> TablesAsync(string connectionString)
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("SELECT array_agg(tablename::text ORDER BY tablename) FROM pg_tables WHERE schemaname = current_schema()", connection);
            return (string[])(await command.ExecuteScalarAsync())!;
        }
    }
}
