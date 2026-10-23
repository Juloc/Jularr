using Jularr.Backend.Data.Enums.Sql;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Tests.Infrastructure;
using Npgsql;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class SqlContextTests
    {
        [TestMethod]
        public async Task ReadOnly_RejectsSequenceMutation_AndDoesNotLeakToNextTransaction()
        {
            await using var database = CreateDataSource();

            await using (var setup = new SqlContext(database))
            {
                await setup.BeginAsync(SqlAccessMode.ReadWrite);
                await setup.RequireLogicSql().ExecuteAsync(new SqlStatement { Statement = "CREATE SEQUENCE \"SqlContextReadOnlySequence\"" });
                await setup.CommitAsync();
            }

            await using (var read = new SqlContext(database))
            {
                await read.BeginAsync(SqlAccessMode.ReadOnly);

                Assert.AreEqual("on", await read.ReadSql.ExecuteScalarAsync(new SqlStatement { Statement = "SELECT current_setting('transaction_read_only')" }));
                Assert.ThrowsExactly<InvalidOperationException>(() => read.RequireLogicSql());

                var error = await Assert.ThrowsExactlyAsync<PostgresException>(
                    () => read.ReadSql.ExecuteScalarAsync(new SqlStatement { Statement = "SELECT nextval('\"SqlContextReadOnlySequence\"')" }));

                Assert.AreEqual("25006", error.SqlState);
            }

            await using (var write = new SqlContext(database))
            {
                await write.BeginAsync(SqlAccessMode.ReadWrite);
                Assert.AreEqual("off", await write.ReadSql.ExecuteScalarAsync(new SqlStatement { Statement = "SELECT current_setting('transaction_read_only')" }));

                await write.RequireLogicSql().ExecuteAsync(new SqlStatement { Statement = "CREATE TEMP TABLE \"SqlContextWriteProbe\" (\"Id\" integer)" });
                await write.CommitAsync();
            }
        }

        [TestMethod]
        public async Task ReadFacade_RejectsMutatingCommandsEvenDuringReadWriteTransaction()
        {
            await using var database = CreateDataSource();
            await using var context = new SqlContext(database);
            await context.BeginAsync(SqlAccessMode.ReadWrite);

            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => context.ReadSql.ExecuteScalarAsync(new SqlStatement { Statement = "UPDATE \"Accounts\" SET \"Id\" = 1 RETURNING \"Id\"" }));

            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => context.ReadSql.ExecuteScalarAsync(new SqlStatement { Statement = "WITH \"Deleted\" AS (DELETE FROM \"Accounts\" RETURNING \"Id\") SELECT * FROM \"Deleted\"" }));

            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => context.ReadSql.ExecuteScalarAsync(new SqlStatement { Statement = "SELECT 1; DELETE FROM \"Accounts\"" }));

            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => context.ReadSql.ExecuteScalarAsync(new SqlStatement { Statement = "SELECT * FROM \"Accounts\" FOR UPDATE" }));

            Assert.AreEqual("UPDATE", await context.ReadSql.ExecuteScalarAsync(new SqlStatement { Statement = "SELECT 'UPDATE'::text /* DROP TABLE \"Accounts\" */" }));
        }

        [TestMethod]
        public async Task ReadWrite_MultipleLogicSteps_UseOneTransactionAndCommitTogether()
        {
            await using var database = CreateDataSource();

            await using (var context = new SqlContext(database))
            {
                await context.BeginAsync(SqlAccessMode.ReadWrite);
                var sql = context.RequireLogicSql();

                await sql.ExecuteAsync(new SqlStatement { Statement = "CREATE TABLE \"SqlContextCommitProbe\" (\"Id\" bigint PRIMARY KEY)" });
                var backendId = await context.ReadSql.ExecuteScalarAsync(new SqlStatement { Statement = "SELECT pg_backend_pid()" });
                var transactionId = await context.ReadSql.ExecuteScalarAsync(new SqlStatement { Statement = "SELECT txid_current()" });

                await sql.ExecuteAsync(new SqlStatement { Statement = "INSERT INTO \"SqlContextCommitProbe\" (\"Id\") VALUES (1)" });
                await sql.ExecuteAsync(new SqlStatement { Statement = "INSERT INTO \"SqlContextCommitProbe\" (\"Id\") VALUES (2)" });

                Assert.AreEqual(backendId, await context.ReadSql.ExecuteScalarAsync(new SqlStatement { Statement = "SELECT pg_backend_pid()" }));
                Assert.AreEqual(transactionId, await context.ReadSql.ExecuteScalarAsync(new SqlStatement { Statement = "SELECT txid_current()" }));
                Assert.AreEqual(2L, await context.ReadSql.ExecuteScalarAsync(new SqlStatement { Statement = "SELECT COUNT(*) FROM \"SqlContextCommitProbe\"" }));

                await context.CommitAsync();
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => context.CommitAsync());
            }

            await using var verify = new SqlContext(database);
            await verify.BeginAsync(SqlAccessMode.ReadOnly);
            Assert.AreEqual(2L, await verify.ReadSql.ExecuteScalarAsync(new SqlStatement { Statement = "SELECT COUNT(*) FROM \"SqlContextCommitProbe\"" }));
        }

        [TestMethod]
        public async Task ReadWrite_FailedLogicStep_RollsBackPreviousChanges()
        {
            await using var database = CreateDataSource();

            await using (var context = new SqlContext(database))
            {
                await context.BeginAsync(SqlAccessMode.ReadWrite);
                var sql = context.RequireLogicSql();

                await sql.ExecuteAsync(new SqlStatement { Statement = "CREATE TABLE \"SqlContextRollbackProbe\" (\"Id\" bigint PRIMARY KEY)" });
                await sql.ExecuteAsync(new SqlStatement { Statement = "INSERT INTO \"SqlContextRollbackProbe\" (\"Id\") VALUES (1)" });

                var error = await Assert.ThrowsExactlyAsync<PostgresException>(
                    () => sql.ExecuteAsync(new SqlStatement { Statement = "INSERT INTO \"SqlContextRollbackProbe\" (\"Id\") VALUES (1)" }));

                Assert.AreEqual("23505", error.SqlState);
            }

            await using var verify = new SqlContext(database);
            await verify.BeginAsync(SqlAccessMode.ReadOnly);

            // Npgsql does not materialize PostgreSQL regclass as System.Object.
            // Query the supported boolean scalar instead of reading the regclass value.
            var missing = await verify.ReadSql.ExecuteScalarAsync(new SqlStatement { Statement = "SELECT to_regclass('\"SqlContextRollbackProbe\"') IS NULL" });
            Assert.AreEqual(true, missing);
        }

        [TestMethod]
        public async Task Context_UnstartedOrCompleted_RejectsQueries()
        {
            await using var database = CreateDataSource();
            await using var context = new SqlContext(database);

            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => context.ReadSql.ExecuteScalarAsync(new SqlStatement { Statement = "SELECT 1" }));

            await context.BeginAsync(SqlAccessMode.ReadWrite);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => context.BeginAsync(SqlAccessMode.ReadWrite));

            await context.CommitAsync();
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => context.ReadSql.ExecuteScalarAsync(new SqlStatement { Statement = "SELECT 1" }));
        }

        private static NpgsqlDataSource CreateDataSource()
        {
            var connectionString = TestPostgres.ResolveConnectionString($"Data Source=sql-context-{Guid.NewGuid():N}");
            var options = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = true };
            return new NpgsqlDataSourceBuilder(options.ConnectionString).Build();
        }
    }
}
