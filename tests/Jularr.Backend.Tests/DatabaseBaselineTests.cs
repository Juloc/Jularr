using Jularr.Backend.Service.Core;
using Jularr.Backend.Tests.Infrastructure;
using Npgsql;

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
            CollectionAssert.IsSubsetOf(new[] { "Accounts", "InstanceSettings", "Works", "WantedItems", "StorageMounts", "StoredFiles", "ReadingLanguages", "ReadingRecognitions" }, tables);

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

        private static async Task<string[]> TablesAsync(string connectionString)
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("SELECT array_agg(tablename::text ORDER BY tablename) FROM pg_tables WHERE schemaname = current_schema()", connection);
            return (string[])(await command.ExecuteScalarAsync())!;
        }
    }
}
