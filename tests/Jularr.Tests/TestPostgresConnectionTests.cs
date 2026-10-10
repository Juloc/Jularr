using Jularr.Tests.Infrastructure;
using Npgsql;

namespace Jularr.Tests;

[TestClass]
public sealed class TestPostgresConnectionTests
{
    [TestMethod]
    public async Task IsolatedConnections_ReleaseSessionsAfterConcurrentUse_AndPreserveDatabaseIdentity()
    {
        var logicalDatabase = $"Data Source=connection-lifecycle-{Guid.NewGuid():N}";
        var connectionString = TestPostgres.ResolveConnectionString(logicalDatabase);
        Assert.IsFalse(new NpgsqlConnectionStringBuilder(connectionString).Pooling);
        Assert.AreEqual(connectionString, TestPostgres.ResolveConnectionString(logicalDatabase));
        Assert.AreEqual(connectionString, TestPostgres.ResolveConnectionString(connectionString));

        var connections = Enumerable.Range(0, 6).Select(_ => new NpgsqlConnection(connectionString)).ToArray();
        var backendIds = new List<int>();
        try
        {
            await Task.WhenAll(connections.Select(connection => connection.OpenAsync()));
            foreach (var connection in connections)
            {
                await using var command = new NpgsqlCommand("SELECT pg_backend_pid()", connection);
                backendIds.Add(Convert.ToInt32(await command.ExecuteScalarAsync()));
            }

            Assert.AreEqual(connections.Length, backendIds.Distinct().Count(), "Concurrency must retain separate PostgreSQL sessions.");
        }
        finally
        {
            foreach (var connection in connections)
            {
                await connection.DisposeAsync();
            }
        }

        await using var observer = new NpgsqlConnection(connectionString);
        await observer.OpenAsync();
        await using var remaining = new NpgsqlCommand("SELECT COUNT(*) FROM pg_stat_activity WHERE pid = ANY(@BackendIds)", observer);
        remaining.Parameters.AddWithValue("BackendIds", backendIds.ToArray());
        Assert.AreEqual(0L, (long)(await remaining.ExecuteScalarAsync())!, "Disposed fixture connections must not consume idle server slots.");
    }
}
