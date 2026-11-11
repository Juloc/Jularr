using Jularr.Backend.Data.Enums.Sql;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Service.Core;
using Jularr.Backend.Tests.Infrastructure;
using Npgsql;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class SqlContextTests
    {
        [TestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public async Task ReadOnly_JitOverride_EndsWithTransaction(bool commit)
        {
            var connectionString = TestPostgres.ResolveConnectionString($"Data Source=sql-jit-{Guid.NewGuid():N}");
            var options = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = true, MaxPoolSize = 1, NoResetOnClose = true };
            await using var database = new NpgsqlDataSourceBuilder(options.ConnectionString).Build();
            await using (var connection = await database.OpenConnectionAsync())
            await using (var configure = new NpgsqlCommand("SET jit = on", connection))
            {
                await configure.ExecuteNonQueryAsync();
            }

            await using (var read = new SqlContext(database))
            {
                read.PrepareReadTransaction(System.Data.IsolationLevel.ReadCommitted);
                Assert.AreEqual("off", await read.ReadSql.ExecuteScalarAsync(new SqlStatement { Statement = "SELECT current_setting('jit')", DisableJit = true }));
                Assert.AreEqual("on", await read.ReadSql.ExecuteScalarAsync(new SqlStatement { Statement = "SELECT current_setting('transaction_read_only')" }));
                Assert.AreEqual("off", await read.ReadSql.ExecuteScalarAsync(new SqlStatement { Statement = "SELECT current_setting('jit')", DisableJit = true }));
                if (commit) await read.CommitAsync();
            }

            // Reuse the same physical connection without the pool resetting its settings.
            await using var reused = await database.OpenConnectionAsync();
            await using var verify = new NpgsqlCommand("SELECT current_setting('jit')", reused);
            Assert.AreEqual("on", await verify.ExecuteScalarAsync());
        }

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

        [TestMethod]
        public async Task Lanes_NameTheirConnections_AndAFullPoolBlocksOnlyItsOwnLane()
        {
            var connectionString = new NpgsqlConnectionStringBuilder(TestPostgres.ResolveConnectionString($"Data Source=sql-lanes-{Guid.NewGuid():N}")) { Pooling = true, Timeout = 1 }.ConnectionString;
            await using var web = SqlDatabase.Create(connectionString, SqlLane.Web, 1, SqlLanes.WebIdleInTransaction, SqlLanes.WebStatementTimeout);
            await using var worker = SqlDatabase.Create(connectionString, SqlLane.Worker, 1, SqlLanes.WorkerIdleInTransaction, SqlLanes.WorkerStatementTimeout);

            // Each lane is named in pg_stat_activity and carries its statement limit, the client waiting a little longer, and the last resort for an idle transaction.
            Assert.AreEqual(20, new NpgsqlConnectionStringBuilder(web.DataSource.ConnectionString).CommandTimeout);
            Assert.AreEqual(65, new NpgsqlConnectionStringBuilder(worker.DataSource.ConnectionString).CommandTimeout);
            await using (var request = new SqlContext(web))
            {
                await request.BeginAsync(SqlAccessMode.ReadOnly);
                Assert.AreEqual("jularr-web", await request.ReadSql.ExecuteScalarAsync(new SqlStatement { Statement = "SELECT current_setting('application_name')" }));
                Assert.AreEqual("10min", await request.ReadSql.ExecuteScalarAsync(new SqlStatement { Statement = "SELECT current_setting('idle_in_transaction_session_timeout')" }));
                Assert.AreEqual("15s", await request.ReadSql.ExecuteScalarAsync(new SqlStatement { Statement = "SELECT current_setting('statement_timeout')" }));

                // The web pool is full: background work still gets its connection, a second request fails as a busy database.
                await using (var background = new SqlContext(worker))
                {
                    await background.BeginAsync(SqlAccessMode.ReadOnly);
                    Assert.AreEqual("jularr-worker", await background.ReadSql.ExecuteScalarAsync(new SqlStatement { Statement = "SELECT current_setting('application_name')" }));
                    Assert.AreEqual("30min", await background.ReadSql.ExecuteScalarAsync(new SqlStatement { Statement = "SELECT current_setting('idle_in_transaction_session_timeout')" }));
                    Assert.AreEqual("1min", await background.ReadSql.ExecuteScalarAsync(new SqlStatement { Statement = "SELECT current_setting('statement_timeout')" }));
                }

                await using var waiting = new SqlContext(web);
                var full = await Assert.ThrowsAsync<NpgsqlException>(() => waiting.BeginAsync(SqlAccessMode.ReadOnly));
                var error = ServiceError.FromException(full);
                Assert.AreEqual(503, error.StatusCode);
                Assert.AreEqual("database_busy", error.Code);
            }

            // The most connections in use since the last look; the next count starts from those in use now.
            Assert.AreEqual(1, web.TakePeak());
            Assert.AreEqual(0, web.TakePeak());
            Assert.AreEqual(1, worker.TakePeak());
        }

        [TestMethod]
        public async Task StatementTimeout_OwnLimitHoldsForItsStatement_ThenTheLaneLimitAgain()
        {
            var connectionString = TestPostgres.ResolveConnectionString($"Data Source=sql-statement-timeout-{Guid.NewGuid():N}");
            await using var lane = SqlDatabase.Create(connectionString, SqlLane.Worker, 2, SqlLanes.WorkerIdleInTransaction, TimeSpan.FromMilliseconds(300));

            // Past the lane's limit the server cancels the statement.
            await using (var plain = new SqlContext(lane))
            {
                await plain.BeginAsync(SqlAccessMode.ReadOnly);
                var cancelled = await Assert.ThrowsExactlyAsync<PostgresException>(() => plain.ReadSql.ExecuteScalarAsync(new SqlStatement { Statement = "SELECT pg_sleep(0.5)::text" }));
                Assert.AreEqual(PostgresErrorCodes.QueryCanceled, cancelled.SqlState);
            }

            // A statement with its own limit runs through; the next one in the transaction has the lane's limit again.
            await using (var known = new SqlContext(lane))
            {
                await known.BeginAsync(SqlAccessMode.ReadOnly);
                Assert.AreEqual("", await known.ReadSql.ExecuteScalarAsync(new SqlStatement { Statement = "SELECT pg_sleep(0.5)::text", StatementTimeout = TimeSpan.FromSeconds(2) }));
                Assert.AreEqual("300ms", await known.ReadSql.ExecuteScalarAsync(new SqlStatement { Statement = "SELECT current_setting('statement_timeout')" }));
            }

            // Npgsql waits past its own command limit for a statement with a longer one.
            var impatient = new NpgsqlConnectionStringBuilder(connectionString) { CommandTimeout = 1 }.ConnectionString;
            await using var client = new NpgsqlDataSourceBuilder(impatient).Build();
            await using var waiting = new SqlContext(client);
            await waiting.BeginAsync(SqlAccessMode.ReadOnly);
            Assert.AreEqual("", await waiting.ReadSql.ExecuteScalarAsync(new SqlStatement { Statement = "SELECT pg_sleep(1.2)::text", StatementTimeout = TimeSpan.FromSeconds(3) }));
        }

        [TestMethod]
        public async Task EndRead_CommitsAndGivesTheConnectionBack_AndNoSqlMayFollow()
        {
            var connectionString = TestPostgres.ResolveConnectionString($"Data Source=sql-end-read-{Guid.NewGuid():N}");
            await using var lane = new SqlDatabase(new NpgsqlDataSourceBuilder(new NpgsqlConnectionStringBuilder(connectionString) { Pooling = true, MaxPoolSize = 1 }.ConnectionString).Build());
            await using var read = new SqlContext(lane);
            read.PrepareReadTransaction(System.Data.IsolationLevel.ReadCommitted);
            Assert.IsFalse(read.InTransaction, "A prepared read holds nothing until its first statement.");
            Assert.AreEqual(1, await read.ReadSql.ExecuteScalarAsync(new SqlStatement { Statement = "SELECT 1" }));
            Assert.IsTrue(read.InTransaction);

            await read.EndReadAsync(CancellationToken.None);
            Assert.IsFalse(read.InTransaction);

            // The pool's only connection is free again for others.
            await using (var other = new SqlContext(lane))
            {
                await other.BeginAsync(SqlAccessMode.ReadOnly);
                Assert.AreEqual(2, await other.ReadSql.ExecuteScalarAsync(new SqlStatement { Statement = "SELECT 2" }));
            }

            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => read.ReadSql.ExecuteScalarAsync(new SqlStatement { Statement = "SELECT 1" }));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => read.RunShortBatchAsync(SqlAccessMode.ReadOnly, _ => Task.FromResult(true), _ => Task.CompletedTask, CancellationToken.None));
            await read.CommitAsync();

            // Only a read ends early.
            await using var write = new SqlContext(lane);
            await write.BeginAsync(SqlAccessMode.ReadWrite);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => write.EndReadAsync(CancellationToken.None));

            // A committed transaction is no longer open, even before the context gives its connection back.
            await write.CommitAsync();
            Assert.IsFalse(write.InTransaction);
        }

        [TestMethod]
        [DataRow(PostgresErrorCodes.QueryCanceled)]
        [DataRow(PostgresErrorCodes.LockNotAvailable)]
        public void ServerCancelOrLockTimeout_IsDatabaseBusy(string sqlState)
        {
            var error = ServiceError.FromException(new PostgresException("canceled", "ERROR", "ERROR", sqlState));
            Assert.AreEqual(503, error.StatusCode);
            Assert.AreEqual("database_busy", error.Code);
            Assert.AreEqual("errors.database_busy", error.ResourceKey);
        }

        private static NpgsqlDataSource CreateDataSource()
        {
            var connectionString = TestPostgres.ResolveConnectionString($"Data Source=sql-context-{Guid.NewGuid():N}");
            var options = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = true };
            return new NpgsqlDataSourceBuilder(options.ConnectionString).Build();
        }
    }
}
