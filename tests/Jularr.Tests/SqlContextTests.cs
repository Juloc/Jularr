using Jularr.Infrastructure.Sql;
using Jularr.Tests.Infrastructure;
using Npgsql;

namespace Jularr.Tests;

[TestClass]
public sealed class SqlContextTests
{
    [TestMethod]
    public async Task ReadOnly_RejectsDdl_AndDoesNotLeakToNextTransaction()
    {
        await using var database = CreateDataSource();

        await using (var read = new SqlContext(database))
        {
            await read.BeginAsync(SqlAccessMode.ReadOnly);

            Assert.AreEqual("on", await read.ReadSql.ExecuteScalarAsync("SHOW transaction_read_only"));
            Assert.ThrowsExactly<InvalidOperationException>(() => read.RequireLogicSql());

            var error = await Assert.ThrowsExactlyAsync<PostgresException>(
                () => read.ReadSql.ExecuteScalarAsync("CREATE TEMP TABLE \"SqlContextReadOnlyProbe\" (\"Id\" integer)"));

            Assert.AreEqual("25006", error.SqlState);
        }

        await using (var write = new SqlContext(database))
        {
            await write.BeginAsync(SqlAccessMode.ReadWrite);
            Assert.AreEqual("off", await write.ReadSql.ExecuteScalarAsync("SHOW transaction_read_only"));

            await write.RequireLogicSql().ExecuteAsync("CREATE TEMP TABLE \"SqlContextWriteProbe\" (\"Id\" integer)");
            await write.CommitAsync();
        }
    }

    [TestMethod]
    public async Task ReadWrite_MultipleLogicSteps_UseOneTransactionAndCommitTogether()
    {
        await using var database = CreateDataSource();

        await using (var context = new SqlContext(database))
        {
            await context.BeginAsync(SqlAccessMode.ReadWrite);
            var sql = context.RequireLogicSql();

            await sql.ExecuteAsync("CREATE TABLE \"SqlContextCommitProbe\" (\"Id\" bigint PRIMARY KEY)");
            var backendId = await context.ReadSql.ExecuteScalarAsync("SELECT pg_backend_pid()");
            var transactionId = await context.ReadSql.ExecuteScalarAsync("SELECT txid_current()");

            await sql.ExecuteAsync("INSERT INTO \"SqlContextCommitProbe\" (\"Id\") VALUES (1)");
            await sql.ExecuteAsync("INSERT INTO \"SqlContextCommitProbe\" (\"Id\") VALUES (2)");

            Assert.AreEqual(backendId, await context.ReadSql.ExecuteScalarAsync("SELECT pg_backend_pid()"));
            Assert.AreEqual(transactionId, await context.ReadSql.ExecuteScalarAsync("SELECT txid_current()"));
            Assert.AreEqual(2L, await context.ReadSql.ExecuteScalarAsync("SELECT COUNT(*) FROM \"SqlContextCommitProbe\""));

            await context.CommitAsync();
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(context.CommitAsync);
        }

        await using var verify = new SqlContext(database);
        await verify.BeginAsync(SqlAccessMode.ReadOnly);
        Assert.AreEqual(2L, await verify.ReadSql.ExecuteScalarAsync("SELECT COUNT(*) FROM \"SqlContextCommitProbe\""));
    }

    [TestMethod]
    public async Task ReadWrite_FailedLogicStep_RollsBackPreviousChanges()
    {
        await using var database = CreateDataSource();

        await using (var context = new SqlContext(database))
        {
            await context.BeginAsync(SqlAccessMode.ReadWrite);
            var sql = context.RequireLogicSql();

            await sql.ExecuteAsync("CREATE TABLE \"SqlContextRollbackProbe\" (\"Id\" bigint PRIMARY KEY)");
            await sql.ExecuteAsync("INSERT INTO \"SqlContextRollbackProbe\" (\"Id\") VALUES (1)");

            var error = await Assert.ThrowsExactlyAsync<PostgresException>(
                () => sql.ExecuteAsync("INSERT INTO \"SqlContextRollbackProbe\" (\"Id\") VALUES (1)"));

            Assert.AreEqual("23505", error.SqlState);
        }

        await using var verify = new SqlContext(database);
        await verify.BeginAsync(SqlAccessMode.ReadOnly);

        var relation = await verify.ReadSql.ExecuteScalarAsync("SELECT to_regclass('\"SqlContextRollbackProbe\"')");
        Assert.IsTrue(relation is null or DBNull);
    }

    [TestMethod]
    public async Task Context_UnstartedOrCompleted_RejectsQueries()
    {
        await using var database = CreateDataSource();
        await using var context = new SqlContext(database);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => context.ReadSql.ExecuteScalarAsync("SELECT 1"));

        await context.BeginAsync(SqlAccessMode.ReadWrite);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => context.BeginAsync(SqlAccessMode.ReadWrite));

        await context.CommitAsync();
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => context.ReadSql.ExecuteScalarAsync("SELECT 1"));
    }

    private static NpgsqlDataSource CreateDataSource()
    {
        var connectionString = TestPostgres.ResolveConnectionString($"Data Source=sql-context-{Guid.NewGuid():N}");
        var options = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = true };
        return new NpgsqlDataSourceBuilder(options.ConnectionString).Build();
    }
}
