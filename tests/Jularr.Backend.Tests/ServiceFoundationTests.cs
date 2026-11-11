using Jularr.Backend.Data.Enums.Sql;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Storage;
using Jularr.Backend.Data.Logic.Storage;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Service.Core;
using Jularr.Backend.Tests.Infrastructure;
using Jularr.Backend.Data;
using Jularr.Integrations.Logic.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class ServiceFoundationTests
    {
        private sealed record AccountParameters(long AccountId);

        private sealed record AccountData(string Name, bool Duplicate = false);

        private sealed record AccountResult(long AccountId, string Name) : IServiceOutput;

        private sealed record PrivateAccountResult(long AccountId, string Name, string Email) : IServiceOutput;

        [TestMethod]
        public async Task ReadService_RequiresAccessAndModulesInReadOnlyTransaction()
        {
            await using var database = CreateDataSource();
            var gate = new TestGate();
            var runtime = new ServiceRuntime(new SqlDatabase(database), gate, new ServiceTimings(TimeProvider.System));
            var service = runtime.Create(new AccountReadService());
            runtime.ValidateAll([service]);

            var result = await service.ExecuteAsync<AccountResult>(new AccountParameters(5));

            Assert.AreEqual(5L, result.AccountId);
            Assert.AreEqual("on", result.Name);
            CollectionAssert.AreEqual(new[] { "caller", "access", "modules" }, gate.Events.ToArray());
        }

        [TestMethod]
        public async Task ModulesBlocked_NeverRunsReadOperation()
        {
            await using var database = CreateDataSource();
            var gate = new TestGate { BlockModule = true };
            var service = new ServiceRuntime(new SqlDatabase(database), gate, new ServiceTimings(TimeProvider.System)).Create(new AccountReadService());

            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => service.ExecuteAsync<AccountResult>(new AccountParameters(5)));

            CollectionAssert.AreEqual(new[] { "caller", "access", "modules" }, gate.Events.ToArray());
        }

        [TestMethod]
        public async Task UndeclaredOutput_IsRejectedWithoutInvokingGate()
        {
            await using var database = CreateDataSource();
            var gate = new TestGate();
            var service = new ServiceRuntime(new SqlDatabase(database), gate, new ServiceTimings(TimeProvider.System)).Create(new AccountReadService());

            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => service.ExecuteAsync<PrivateAccountResult>(new AccountParameters(5)));

            Assert.AreEqual(0, gate.Events.Count);
        }

        [TestMethod]
        public async Task UserList_InvalidSortAndPage_FailBeforeCallerOrDatabase()
        {
            await using var database = CreateDataSource();
            var gate = new TestGate();
            var service = new ServiceRuntime(new SqlDatabase(database), gate, new ServiceTimings(TimeProvider.System)).Create(new AccountListService());

            await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(
                () => service.ExecuteAsync<PageResult<AccountResult>>(new ListParameters(1, 25, (ListSort)255)));
            Assert.AreEqual(0, gate.Events.Count);

            gate.Events.Clear();
            await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(
                () => service.ExecuteAsync<PageResult<AccountResult>>(new ListParameters(1, 101, ListSort.AccountId)));
            Assert.AreEqual(0, gate.Events.Count);
        }

        [TestMethod]
        public async Task UserList_PageHook_IsEvaluatedOnceAndUsedBySql()
        {
            await using var database = CreateDataSource();
            var service = new ServiceRuntime(new SqlDatabase(database), new TestGate(), new ServiceTimings(TimeProvider.System)).Create(new AccountListService());

            var result = await service.ExecuteAsync<PageResult<AccountResult>>(new ListParameters(2, 1, null));

            Assert.AreEqual(2L, result.Items.Single().AccountId);
            Assert.AreEqual(2, result.Page);
            Assert.AreEqual(1, service.PageRequests);
        }

        [TestMethod]
        public async Task AdminWrite_MultipleLogicCallsRollbackOnLateFailure()
        {
            await using var database = CreateDataSource();
            await using (var seed = new SqlContext(database))
            {
                await seed.BeginAsync(SqlAccessMode.ReadWrite);
                await seed.RequireLogicSql().ExecuteAsync(new SqlStatement { Statement = "CREATE TABLE \"ServiceFoundationProbe\" (\"Id\" bigint PRIMARY KEY, \"Name\" text NOT NULL)" });
                await seed.CommitAsync();
            }

            var gate = new TestGate { Area = ServiceArea.Admin };
            var service = new ServiceRuntime(new SqlDatabase(database), gate, new ServiceTimings(TimeProvider.System)).Create(new AccountWriteService(new AccountWorkflowLogic()));

            var error = await Assert.ThrowsExactlyAsync<PostgresException>(
                () => service.ExecuteAsync<AccountResult>(new AccountParameters(5), new AccountData("test", Duplicate: true)));
            Assert.AreEqual("23505", error.SqlState);

            await using var verify = new SqlContext(database);
            await verify.BeginAsync(SqlAccessMode.ReadOnly);
            Assert.AreEqual(0L, await verify.ReadSql.ExecuteScalarAsync(new SqlStatement { Statement = "SELECT COUNT(*) FROM \"ServiceFoundationProbe\"" }));
            CollectionAssert.AreEqual(new[] { "caller", "access", "transaction", "modules" }, gate.Events.ToArray());
        }

        [TestMethod]
        public async Task ExecuteWithoutOuterTransaction_UsesShortBatchesWithIndependentRollback()
        {
            await using var database = CreateDataSource();

            await using (var setup = new SqlContext(database))
            {
                await setup.BeginAsync(SqlAccessMode.ReadWrite);
                await setup.RequireLogicSql().ExecuteAsync(new SqlStatement { Statement = "CREATE TABLE \"ServiceExecuteBatchProbe\" (\"Id\" bigint PRIMARY KEY)" });
                await setup.CommitAsync();
            }

            var gate = new TestGate { Area = ServiceArea.System };
            var service = new ServiceRuntime(new SqlDatabase(database), gate, new ServiceTimings(TimeProvider.System)).Create(new SystemBatchService());

            var error = await Assert.ThrowsExactlyAsync<PostgresException>(
                () => service.ExecuteAsync<AccountResult>(new AccountParameters(9), new AccountData("batch")));
            Assert.AreEqual("23505", error.SqlState);

            await using var verify = new SqlContext(database);
            await verify.BeginAsync(SqlAccessMode.ReadOnly);
            Assert.AreEqual(1L, await verify.ReadSql.ExecuteScalarAsync(new SqlStatement { Statement = "SELECT COUNT(*) FROM \"ServiceExecuteBatchProbe\"" }));
            Assert.AreEqual(1L, await verify.ReadSql.ExecuteScalarAsync(new SqlStatement { Statement = "SELECT \"Id\" FROM \"ServiceExecuteBatchProbe\"" }));

            CollectionAssert.AreEqual(new[] { "caller", "access", "modules", "transaction", "transaction" }, gate.Events.ToArray());
        }

        [TestMethod]
        public async Task ExecuteBatch_RechecksPermissionsAndBlocksSecondBatchBeforeSql()
        {
            await using var database = CreateDataSource();

            await using (var setup = new SqlContext(database))
            {
                await setup.BeginAsync(SqlAccessMode.ReadWrite);
                await setup.RequireLogicSql().ExecuteAsync(new SqlStatement { Statement = "CREATE TABLE \"ServiceExecuteBatchProbe\" (\"Id\" bigint PRIMARY KEY)" });
                await setup.CommitAsync();
            }

            var gate = new TestGate { Area = ServiceArea.System, BlockSecondTransaction = true };
            var service = new ServiceRuntime(new SqlDatabase(database), gate, new ServiceTimings(TimeProvider.System)).Create(new SystemBatchService());

            await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(
                () => service.ExecuteAsync<AccountResult>(new AccountParameters(9), new AccountData("blocked")));

            await using var verify = new SqlContext(database);
            await verify.BeginAsync(SqlAccessMode.ReadOnly);
            Assert.AreEqual(1L, await verify.ReadSql.ExecuteScalarAsync(new SqlStatement { Statement = "SELECT COUNT(*) FROM \"ServiceExecuteBatchProbe\"" }));
            CollectionAssert.AreEqual(new[] { "caller", "access", "modules", "transaction", "transaction" }, gate.Events.ToArray());
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task Execute_ReadBatchIsRestrictedAndNextWriteCommits(bool attemptMutation)
        {
            await using var database = CreateDataSource();
            await using (var setup = new SqlContext(database))
            {
                await setup.BeginAsync(SqlAccessMode.ReadWrite);
                await setup.RequireLogicSql().ExecuteAsync(new SqlStatement { Statement = "CREATE TABLE \"ServiceExecuteReadProbe\" (\"Id\" bigint PRIMARY KEY); CREATE SEQUENCE \"ServiceExecuteReadSequence\"" });
                await setup.CommitAsync();
            }
            var gate = new TestGate { Area = ServiceArea.System };
            var service = new ServiceRuntime(new SqlDatabase(database), gate, new ServiceTimings(TimeProvider.System)).Create(new ReadBatchService(attemptMutation));
            await service.ExecuteAsync<AccountResult>(new AccountParameters(9), default(NoData));

            CollectionAssert.AreEqual(new[] { SqlAccessMode.ReadOnly, SqlAccessMode.ReadOnly, SqlAccessMode.ReadWrite }, gate.TransactionModes.ToArray());
            await using var verify = new SqlContext(database);
            await verify.BeginAsync(SqlAccessMode.ReadOnly);
            Assert.AreEqual(1L, await verify.ReadSql.ExecuteScalarAsync(new SqlStatement { Statement = "SELECT count(*) FROM \"ServiceExecuteReadProbe\"" }));
            Assert.AreEqual(false, await verify.ReadSql.ExecuteScalarAsync(new SqlStatement { Statement = "SELECT is_called FROM \"ServiceExecuteReadSequence\"" }));
        }

        [TestMethod]
        public async Task ReadService_CannotObtainLogicWriteCapability()
        {
            await using var database = CreateDataSource();
            var service = new ServiceRuntime(new SqlDatabase(database), new TestGate(), new ServiceTimings(TimeProvider.System)).Create(new ReadWithForbiddenLogicService());

            var result = await service.ExecuteAsync<AccountResult>(new AccountParameters(6));
            Assert.AreEqual("blocked", result.Name);
        }

        private enum ListSort : byte
        {
            AccountId,
            DisplayName
        }

        [TestMethod]
        public async Task ValidatePermissions_Denied_NeverRunsOperationHook()
        {
            await using var database = CreateDataSource();
            var gate = new TestGate();
            var service = new ServiceRuntime(new SqlDatabase(database), gate, new ServiceTimings(TimeProvider.System)).Create(new HookReadService());

            await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(
                () => service.ExecuteAsync<AccountResult>(new AccountParameters(999)));

            Assert.AreEqual(0, service.HookCalls);
            Assert.ThrowsExactly<InvalidOperationException>(() => service.ReadBoundParameters());
            CollectionAssert.AreEqual(new[] { "caller", "access", "modules" }, gate.Events.ToArray());
        }

        [TestMethod]
        public async Task OnRead_ConcurrentCalls_KeepInvocationStateIsolated()
        {
            await using var database = CreateDataSource();
            var service = new ServiceRuntime(new SqlDatabase(database), new TestGate(), new ServiceTimings(TimeProvider.System)).Create(new HookReadService());
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var first = service.ExecuteAsync<AccountResult>(new AccountParameters(11), timeout.Token);
            var second = service.ExecuteAsync<AccountResult>(new AccountParameters(22), timeout.Token);
            try
            {
                await service.Started.WaitAsync(timeout.Token);
                await service.Started.WaitAsync(timeout.Token);
            }
            finally
            {
                service.Continue.TrySetResult();
            }

            var results = await Task.WhenAll(first, second);
            CollectionAssert.AreEqual(new long[] { 11, 22 }, results.Select(result => result.AccountId).ToArray());
            Assert.AreEqual(2, service.HookCalls);
            Assert.ThrowsExactly<InvalidOperationException>(() => service.ReadBoundParameters());
        }

        [TestMethod]
        public async Task Read_StatementTheServerCancels_IsDatabaseBusy_AndTheLogNamesServiceAndLane()
        {
            var connectionString = TestPostgres.ResolveConnectionString($"Data Source=service-foundation-{Guid.NewGuid():N}");
            await using var database = new NpgsqlDataSourceBuilder(new NpgsqlConnectionStringBuilder(connectionString) { Options = "-c statement_timeout=100" }.ConnectionString).Build();
            var logs = new ListLogger();
            var service = new ServiceRuntime(new SqlDatabase(database, SqlLane.Worker), new TestGate(), new ServiceTimings(TimeProvider.System), logs).Create(new SleepService());

            var cancelled = await Assert.ThrowsExactlyAsync<PostgresException>(() => service.ExecuteAsync<AccountResult>(new AccountParameters(5)));
            Assert.AreEqual(PostgresErrorCodes.QueryCanceled, cancelled.SqlState);
            Assert.AreEqual("database_busy", ServiceError.FromException(cancelled).Code);
            Assert.AreEqual(503, ServiceError.FromException(cancelled).StatusCode);
            var line = logs.Messages.Single();
            Assert.IsTrue(line.Contains("User/SleepService", StringComparison.Ordinal) && line.Contains("jularr-worker", StringComparison.Ordinal) && line.Contains("57014", StringComparison.Ordinal), line);
        }

        [TestMethod]
        public async Task Read_TransactionTheServerEndsWhileIdle_IsLoggedWithServiceAndLane()
        {
            var connectionString = TestPostgres.ResolveConnectionString($"Data Source=service-foundation-{Guid.NewGuid():N}");
            await using var database = SqlDatabase.Create(connectionString, SqlLane.Worker, 2, TimeSpan.FromMilliseconds(200), SqlLanes.WorkerStatementTimeout);
            var logs = new ListLogger();
            var service = new ServiceRuntime(database, new TestGate(), new ServiceTimings(TimeProvider.System), logs).Create(new SleepService(idleBetween: true));

            var ended = await Assert.ThrowsAsync<NpgsqlException>(() => service.ExecuteAsync<AccountResult>(new AccountParameters(5)));
            Assert.AreEqual(500, ServiceError.FromException(ended).StatusCode);
            var line = logs.Messages.Single();
            Assert.IsTrue(line.Contains("User/SleepService", StringComparison.Ordinal) && line.Contains("jularr-worker", StringComparison.Ordinal) && line.Contains("25P03", StringComparison.Ordinal), line);
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task Read_PastTheLaneLimit_IsDatabaseBusyAtTheLimit_UnlessTheStatementHasItsOwn(bool ownLimit)
        {
            var connectionString = TestPostgres.ResolveConnectionString($"Data Source=service-foundation-{Guid.NewGuid():N}");
            await using var database = SqlDatabase.Create(connectionString, SqlLane.Web, 2, SqlLanes.WebIdleInTransaction, TimeSpan.FromMilliseconds(300));
            var service = new ServiceRuntime(database, new TestGate(), new ServiceTimings(TimeProvider.System))
                .Create(new SleepService(statementTimeout: ownLimit ? TimeSpan.FromSeconds(5) : null));

            if (ownLimit)
            {
                Assert.AreEqual("slept", (await service.ExecuteAsync<AccountResult>(new AccountParameters(5))).Name);
                return;
            }

            // The server cancels at its limit; the client would wait five seconds longer.
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var busy = await Assert.ThrowsExactlyAsync<PostgresException>(() => service.ExecuteAsync<AccountResult>(new AccountParameters(5)));
            Assert.IsTrue(System.Diagnostics.Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(2));
            Assert.AreEqual(503, ServiceError.FromException(busy).StatusCode);
            Assert.AreEqual("database_busy", ServiceError.FromException(busy).Code);
        }

        private sealed class SleepService(bool idleBetween = false, TimeSpan? statementTimeout = null) : UserService<AccountParameters>
        {
            protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<AccountResult>()];

            protected override ModuleRequirement GetInstanceModules(AccountParameters parameters) => ModuleRequirement.None;

            protected override ServicePermission GetPermission(AccountParameters parameters, Type resultType) => new("account.read");

            protected override async Task<IServiceOutput> OnRead()
            {
                // Waiting outside the database with the transaction open, or one statement that runs long.
                if (idleBetween)
                {
                    await Context.ReadSql.ExecuteScalarAsync(new SqlStatement { Statement = "SELECT 1" }, CancellationToken);
                    await Task.Delay(TimeSpan.FromMilliseconds(600), CancellationToken);
                    await Context.ReadSql.ExecuteScalarAsync(new SqlStatement { Statement = "SELECT 1" }, CancellationToken);
                }
                else
                {
                    await Context.ReadSql.ExecuteScalarAsync(new SqlStatement { Statement = "SELECT pg_sleep(0.6)::text", StatementTimeout = statementTimeout }, CancellationToken);
                }

                return new AccountResult(Parameters.AccountId, "slept");
            }
        }

        private sealed class ListLogger : ILogger
        {
            public List<string> Messages { get; } = [];

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (Messages)
                {
                    Messages.Add(formatter(state, exception));
                }
            }
        }

        private sealed class HookReadService : UserService<AccountParameters>
        {
            private int _hookCalls;
            public int HookCalls => _hookCalls;
            public SemaphoreSlim Started { get; } = new(0);
            public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public AccountParameters ReadBoundParameters() => Parameters;

            protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<AccountResult>()];
            protected override ModuleRequirement GetInstanceModules(AccountParameters parameters) => ModuleRequirement.None;
            protected override ServicePermission GetPermission(AccountParameters parameters, Type resultType) => new("account.read");


            protected override Task ValidatePermissions(AccountParameters parameters)
            {
                Assert.AreEqual(1L, Auth.AccountId);
                Assert.AreSame(parameters, Parameters);
                if (parameters.AccountId == 999)
                {
                    throw new UnauthorizedAccessException("The requested account is unavailable.");
                }

                return Task.CompletedTask;
            }

            protected override async Task<IServiceOutput> OnRead()
            {
                Interlocked.Increment(ref _hookCalls);
                var parameters = Parameters;
                var context = Context;
                var caller = Auth;
                var token = CancellationToken;
                Started.Release();
                await Continue.Task.WaitAsync(token);
                Assert.AreSame(parameters, Parameters);
                Assert.AreSame(context, Context);
                Assert.AreSame(caller, Auth);
                Assert.AreEqual(token, CancellationToken);
                var id = await Context.ReadSql.ExecuteScalarAsync(new SqlStatement { Statement = "SELECT @AccountId::bigint", Parameters = { Parameters } }, CancellationToken);
                return new AccountResult((long)id!, "hook");
            }
        }

        private sealed record ListParameters(int Page, int PageSize, ListSort? Sort);

        private sealed class AccountListService : UserListService<ListParameters, ListSort>
        {
            public int PageRequests { get; private set; }

            protected override IReadOnlyList<ServiceSortKey<ListSort>> GetSortKeys() =>
            [
                ServiceSortKey<ListSort>.Default(ListSort.AccountId),
                ServiceSortKey<ListSort>.Additional(ListSort.DisplayName)
            ];

            protected override ListSort? GetRequestedSort(ListParameters parameters) => parameters.Sort;
            protected override PageRequest GetPageRequest(ListParameters parameters)
            {
                PageRequests++;
                return new PageRequest(parameters.Page, parameters.PageSize);
            }
            protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<PageResult<AccountResult>>()];
            protected override ModuleRequirement GetInstanceModules(ListParameters parameters) => ModuleRequirement.None;
            protected override ServicePermission GetPermission(ListParameters parameters, Type resultType) => new("accounts.list");

            protected override async Task<IServiceOutput> OnRead()
            {
                var sql = new SqlStatement();
                sql.Statement =
                    """
                    SELECT
                        item."Id" AS "AccountId",
                        'test'::text AS "Name"
                    FROM
                        (VALUES (1::bigint), (2::bigint), (3::bigint)) AS item("Id")
                    ORDER BY
                        item."Id"
                    LIMIT @PageSize
                    OFFSET @Offset
                    """;
                sql.Page = Page;

                return await Context.ReadSql.ReadPageAsync<AccountResult>(sql, CancellationToken);
            }
        }

        private sealed class ReadWithForbiddenLogicService : UserService<AccountParameters>
        {
            protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<AccountResult>()];
            protected override ModuleRequirement GetInstanceModules(AccountParameters parameters) => ModuleRequirement.None;
            protected override ServicePermission GetPermission(AccountParameters parameters, Type resultType) => new("account.read");

            protected override async Task<IServiceOutput> OnRead()
            {
                Assert.ThrowsExactly<InvalidOperationException>(() => _ = Context.Sql);
                var sql = new SqlStatement();
                sql.Statement = "DELETE FROM \"Profiles\"";
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => Context.ExecuteAsyncForAffectedRows(sql));
                return new AccountResult(Parameters.AccountId, "blocked");
            }
        }

        [TestMethod]
        public async Task TransactionMarker_IsSetWhileATransactionIsOpen_AndClearedBetweenBatchesAndAfterEndRead()
        {
            await using var database = CreateDataSource();
            var runtime = new ServiceRuntime(new SqlDatabase(database), new TestGate { Area = ServiceArea.System }, new ServiceTimings(TimeProvider.System));

            // A read opens its transaction with the first statement and gives it back early; SQL afterwards throws.
            var read = runtime.Create(new MarkerReadService());
            await read.ExecuteAsync<AccountResult>(new AccountParameters(1), default(NoData));
            CollectionAssert.AreEqual(new string?[] { null, "System/MarkerReadService", null }, read.Seen);

            // A write holds its transaction for the whole hook.
            var write = runtime.Create(new MarkerWriteService());
            await write.ExecuteAsync<AccountResult>(new AccountParameters(1), default(NoData));
            CollectionAssert.AreEqual(new string?[] { "System/MarkerWriteService" }, write.Seen);

            // An Execute service holds one only inside each batch.
            var execute = runtime.Create(new MarkerExecuteService());
            await execute.ExecuteAsync<AccountResult>(new AccountParameters(1), default(NoData));
            CollectionAssert.AreEqual(new string?[] { null, "System/MarkerExecuteService", null }, execute.Seen);
            Assert.IsNull(TransactionMarker.OpenTransactionOwner);
        }

        [TestMethod]
        public async Task Prepare_RunsBeforeTheTransaction_ReadsInShortBatches_AndHandsItsResultToTheHook()
        {
            await using var database = CreateDataSource();
            var gate = new TestGate { Area = ServiceArea.System };
            var service = new ServiceRuntime(new SqlDatabase(database), gate, new ServiceTimings(TimeProvider.System)).Create(new PreparingWriteService(gate));

            var result = await service.ExecuteAsync<AccountResult>(new AccountParameters(4), default(NoData));

            Assert.AreEqual("prepared 7", result.Name);
            CollectionAssert.AreEqual(new[] { "caller", "access", "prepare", "transaction", "transaction", "modules", "prepared" }, gate.Events.ToArray());
            CollectionAssert.AreEqual(new[] { SqlAccessMode.ReadOnly, SqlAccessMode.ReadWrite }, gate.TransactionModes.ToArray());
        }

        [TestMethod]
        public async Task ExternalCall_InsideATransaction_FailsInTests_AndIsLoggedOncePerServiceInProduction()
        {
            await using var database = CreateDataSource();
            var runtime = new ServiceRuntime(new SqlDatabase(database), new TestGate { Area = ServiceArea.System }, new ServiceTimings(TimeProvider.System));
            using var strict = new HttpClient(new ExternalCallGuard(true, new ListLogger()) { InnerHandler = new Answering() });

            // Inside an update's transaction the call fails; between an Execute service's batches it goes ahead, inside one it fails.
            var update = runtime.Create(new CallingService(strict, ServiceOperationType.Update));
            var refused = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => update.ExecuteAsync<AccountResult>(new AccountParameters(1), default(NoData)));
            StringAssert.Contains(refused.Message, "external_call_in_transaction: System/CallingService");
            var execute = runtime.Create(new CallingService(strict, ServiceOperationType.Execute));
            Assert.AreEqual("called", (await execute.ExecuteAsync<AccountResult>(new AccountParameters(1), default(NoData))).Name);
            Assert.AreEqual(1, execute.InsideBatchRefusals);

            // A batch running beside the call on the same context (a periodic save) holds its transaction for its own call path only.
            var beside = runtime.Create(new CallingService(strict, ServiceOperationType.Execute, besideABatch: true));
            Assert.AreEqual("called", (await beside.ExecuteAsync<AccountResult>(new AccountParameters(1), default(NoData))).Name);

            // Production lets it go ahead and logs it once for the service.
            var logger = new ListLogger();
            using var lenient = new HttpClient(new ExternalCallGuard(false, logger) { InnerHandler = new Answering() });
            var logged = runtime.Create(new CallingService(lenient, ServiceOperationType.Update));
            await logged.ExecuteAsync<AccountResult>(new AccountParameters(1), default(NoData));
            await logged.ExecuteAsync<AccountResult>(new AccountParameters(1), default(NoData));
            Assert.AreEqual(1, logger.Messages.Count(message => message.Contains("System/CallingService calls example.test", StringComparison.Ordinal)));
        }

        [TestMethod]
        public async Task Storage_InsideATransaction_FailsInTests_AndIsLoggedOncePerServiceInProduction()
        {
            await using var database = CreateDataSource();
            var runtime = new ServiceRuntime(new SqlDatabase(database), new TestGate { Area = ServiceArea.System }, new ServiceTimings(TimeProvider.System));
            using var folder = new LibraryTests.TemporaryDirectory();
            using var shares = new SmbShares(TimeProvider.System);
            using var streams = new StorageStreamServer();
            var strict = new StorageAccess(Options.Create(new StorageOptions()), shares, streams, new EphemeralDataProtectionProvider());

            // An update holds its transaction for the whole hook, so opening its storage fails.
            var update = runtime.Create(new StorageService(strict, folder.Path, ServiceOperationType.Update));
            var refused = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => update.ExecuteAsync<AccountResult>(new AccountParameters(1), default(NoData)));
            StringAssert.Contains(refused.Message, "storage_in_transaction: System/StorageService opens storage NAS");

            // A read reaches the storage once it has ended its read (FFmpeg's input included); an Execute service between its batches, never inside one.
            var read = runtime.Create(new StorageService(strict, folder.Path, ServiceOperationType.Read));
            Assert.AreEqual("opened", (await read.ExecuteAsync<AccountResult>(new AccountParameters(1), default(NoData))).Name);
            Assert.AreEqual(2, read.Refusals);
            var execute = runtime.Create(new StorageService(strict, folder.Path, ServiceOperationType.Execute));
            Assert.AreEqual("opened", (await execute.ExecuteAsync<AccountResult>(new AccountParameters(1), default(NoData))).Name);
            Assert.AreEqual(2, execute.Refusals);

            // Production lets it go ahead and logs it once for the service.
            var logger = new ListLogger();
            var lenient = new StorageAccess(Options.Create(new StorageOptions()), shares, streams, new EphemeralDataProtectionProvider(),
                new HostingEnvironment { EnvironmentName = Environments.Production }, new TypedLogger<StorageAccess>(logger));
            var logged = runtime.Create(new StorageService(lenient, folder.Path, ServiceOperationType.Update));
            await logged.ExecuteAsync<AccountResult>(new AccountParameters(1), default(NoData));
            await logged.ExecuteAsync<AccountResult>(new AccountParameters(1), default(NoData));
            Assert.AreEqual(1, logger.Messages.Count(message => message.Contains("System/StorageService opens storage NAS", StringComparison.Ordinal)));
        }

        private sealed class TypedLogger<T>(ILogger inner) : ILogger<T>
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);
            public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                inner.Log(logLevel, eventId, state, exception, formatter);
        }

        private sealed class StorageService(StorageAccess storage, string folder, ServiceOperationType operation) : SystemService<AccountParameters, NoData>
        {
            private readonly StorageMountData _mount = new(1, "NAS", StorageProtocolType.Local, null, folder, null, null, null, false, null, null, null, false, 1);

            public int Refusals { get; private set; }
            protected override ServiceOperationType GetOperationType() => operation;
            protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<AccountResult>()];
            protected override ModuleRequirement GetInstanceModules(AccountParameters parameters) => ModuleRequirement.None;
            protected override ServicePermission GetPermission(AccountParameters parameters, Type resultType) =>
                new(operation == ServiceOperationType.Read ? "account.read" : operation == ServiceOperationType.Execute ? "account.system.execute" : "account.update");

            protected override Task<IServiceOutput> OnUpdate()
            {
                _ = storage.Open(_mount);
                return Task.FromResult<IServiceOutput>(new AccountResult(Parameters.AccountId, "opened"));
            }

            protected override async Task<IServiceOutput> OnRead()
            {
                await Context.ReadSql.ExecuteScalarAsync(new SqlStatement { Statement = "SELECT 1" }, CancellationToken);
                Refuse();
                await Context.EndReadAsync(CancellationToken);
                return Reach();
            }

            protected override async Task<IServiceOutput> OnExecute()
            {
                await Context.ExecuteBatchAsync((_, _) =>
                {
                    Refuse();
                    return Task.FromResult(true);
                }, CancellationToken);
                return Reach();
            }

            private void Refuse()
            {
                Assert.ThrowsExactly<InvalidOperationException>(() => storage.Open(_mount));
                Assert.ThrowsExactly<InvalidOperationException>(() => storage.Input(new LocalStorageFiles(folder), "film.mkv"));
                Refusals += 2;
            }

            private AccountResult Reach()
            {
                File.WriteAllText(Path.Combine(folder, "film.mkv"), "film");
                using var input = storage.Input(storage.Open(_mount), "film.mkv");
                Assert.AreEqual(Path.Combine(folder, "film.mkv"), input.Location);
                return new AccountResult(Parameters.AccountId, "opened");
            }
        }

        private sealed class Answering : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }

        private sealed class CallingService(HttpClient http, ServiceOperationType operation, bool besideABatch = false) : SystemService<AccountParameters, NoData>
        {
            public int InsideBatchRefusals { get; private set; }
            protected override ServiceOperationType GetOperationType() => operation;
            protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<AccountResult>()];
            protected override ModuleRequirement GetInstanceModules(AccountParameters parameters) => ModuleRequirement.None;
            protected override ServicePermission GetPermission(AccountParameters parameters, Type resultType) => new("account.update");

            protected override async Task<IServiceOutput> OnUpdate()
            {
                (await http.GetAsync("https://example.test/", CancellationToken)).Dispose();
                return new AccountResult(Parameters.AccountId, "called");
            }

            protected override async Task<IServiceOutput> OnExecute()
            {
                if (besideABatch)
                {
                    var inside = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var saving = Task.Run(() => Context.ExecuteBatchAsync(async (_, _) =>
                    {
                        inside.SetResult();
                        await release.Task;
                        return true;
                    }, CancellationToken));
                    await inside.Task;
                    try
                    {
                        (await http.GetAsync("https://example.test/", CancellationToken)).Dispose();
                    }
                    finally
                    {
                        release.SetResult();
                        await saving;
                    }

                    return new AccountResult(Parameters.AccountId, "called");
                }

                await Context.ExecuteBatchAsync(async (_, ct) =>
                {
                    await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => http.GetAsync("https://example.test/", ct));
                    InsideBatchRefusals++;
                    return true;
                }, CancellationToken);
                (await http.GetAsync("https://example.test/", CancellationToken)).Dispose();
                return new AccountResult(Parameters.AccountId, "called");
            }
        }

        private sealed class MarkerReadService : SystemService<AccountParameters, NoData>
        {
            public List<string?> Seen { get; } = [];
            protected override ServiceOperationType GetOperationType() => ServiceOperationType.Read;
            protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<AccountResult>()];
            protected override ModuleRequirement GetInstanceModules(AccountParameters parameters) => ModuleRequirement.None;
            protected override ServicePermission GetPermission(AccountParameters parameters, Type resultType) => new("account.read");

            protected override async Task<IServiceOutput> OnRead()
            {
                Seen.Add(TransactionMarker.OpenTransactionOwner);
                await Context.ReadSql.ExecuteScalarAsync(new SqlStatement { Statement = "SELECT 1" }, CancellationToken);
                Seen.Add(TransactionMarker.OpenTransactionOwner);
                await Context.EndReadAsync(CancellationToken);
                Seen.Add(TransactionMarker.OpenTransactionOwner);
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => Context.ReadSql.ExecuteScalarAsync(new SqlStatement { Statement = "SELECT 1" }, CancellationToken));
                return new AccountResult(Parameters.AccountId, "read");
            }
        }

        private sealed class MarkerWriteService : SystemService<AccountParameters, NoData>
        {
            public List<string?> Seen { get; } = [];
            protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
            protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<AccountResult>()];
            protected override ModuleRequirement GetInstanceModules(AccountParameters parameters) => ModuleRequirement.None;
            protected override ServicePermission GetPermission(AccountParameters parameters, Type resultType) => new("account.update");

            protected override async Task<IServiceOutput> OnUpdate()
            {
                Seen.Add(TransactionMarker.OpenTransactionOwner);
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => Context.EndReadAsync(CancellationToken));
                return new AccountResult(Parameters.AccountId, "written");
            }
        }

        private sealed class MarkerExecuteService : SystemService<AccountParameters, NoData>
        {
            public List<string?> Seen { get; } = [];
            protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
            protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<AccountResult>()];
            protected override ModuleRequirement GetInstanceModules(AccountParameters parameters) => ModuleRequirement.None;
            protected override ServicePermission GetPermission(AccountParameters parameters, Type resultType) => new("account.system.execute");

            protected override async Task<IServiceOutput> OnExecute()
            {
                Seen.Add(TransactionMarker.OpenTransactionOwner);
                await Context.ExecuteBatchAsync((_, _) =>
                {
                    Seen.Add(TransactionMarker.OpenTransactionOwner);
                    return Task.FromResult(true);
                }, CancellationToken);
                Seen.Add(TransactionMarker.OpenTransactionOwner);
                return new AccountResult(Parameters.AccountId, "executed");
            }
        }

        private sealed class PreparingWriteService(TestGate gate) : SystemService<AccountParameters, NoData>
        {
            protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
            protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<AccountResult>()];
            protected override ModuleRequirement GetInstanceModules(AccountParameters parameters) => ModuleRequirement.None;
            protected override ServicePermission GetPermission(AccountParameters parameters, Type resultType) => new("account.update");

            protected override async Task<object?> OnPrepareAsync()
            {
                gate.Events.Add("prepare");
                Assert.IsNull(TransactionMarker.OpenTransactionOwner);
                Assert.ThrowsExactly<InvalidOperationException>(() => _ = Context.Sql);
                var stored = await Context.ExecuteBatchAsync(async (ctx, ct) => (int)(await ctx.ReadSql.ExecuteScalarAsync(new SqlStatement { Statement = "SELECT 7" }, ct))!,
                    CancellationToken, SqlAccessMode.ReadOnly);
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => Context.ExecuteBatchAsync((_, _) => Task.FromResult(true), CancellationToken));
                return stored;
            }

            protected override Task<IServiceOutput> OnUpdate()
            {
                gate.Events.Add("prepared");
                return Task.FromResult<IServiceOutput>(new AccountResult(Parameters.AccountId, "prepared " + (int)Prepared!));
            }
        }

        [TestMethod]
        public async Task Execute_DisabledModule_NeverRunsOperationHook()
        {
            await using var database = CreateDataSource();
            var gate = new TestGate { Area = ServiceArea.System, BlockModule = true };
            var service = new ServiceRuntime(new SqlDatabase(database), gate, new ServiceTimings(TimeProvider.System)).Create(new SystemBatchService(requireModule: true));

            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.ExecuteAsync<AccountResult>(new AccountParameters(9), new AccountData("blocked")));
            Assert.AreEqual(0, service.HookCalls);
            CollectionAssert.AreEqual(new[] { "caller", "access", "transaction", "modules" }, gate.Events.ToArray());
        }

        private sealed class ReadBatchService(bool attemptMutation) : SystemService<AccountParameters, NoData>
        {
            protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
            protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<AccountResult>()];
            protected override ModuleRequirement GetInstanceModules(AccountParameters parameters) => ModuleRequirement.All("Library");
            protected override ServicePermission GetPermission(AccountParameters parameters, Type resultType) => new("account.system.execute");

            protected override async Task<IServiceOutput> OnExecute()
            {
                var reading = Context.ExecuteBatchAsync(async (ctx, ct) =>
                {
                    Assert.AreEqual("on", await ctx.ReadSql.ExecuteScalarAsync(new SqlStatement { Statement = "SELECT current_setting('transaction_read_only')" }, ct));
                    var backend = await ctx.ReadSql.ExecuteScalarAsync(new SqlStatement { Statement = "SELECT pg_backend_pid()" }, ct);
                    var transaction = await ctx.ReadSql.ExecuteScalarAsync(new SqlStatement { Statement = "SELECT txid_current()" }, ct);
                    Assert.AreEqual(backend, await ctx.ReadSql.ExecuteScalarAsync(new SqlStatement { Statement = "SELECT pg_backend_pid()" }, ct));
                    Assert.AreEqual(transaction, await ctx.ReadSql.ExecuteScalarAsync(new SqlStatement { Statement = "SELECT txid_current()" }, ct));
                    Assert.ThrowsExactly<InvalidOperationException>(() => _ = ctx.Sql);
                    if (attemptMutation)
                    {
                        await ctx.ReadSql.ExecuteScalarAsync(new SqlStatement { Statement = "SELECT nextval('\"ServiceExecuteReadSequence\"')" }, ct);
                    }
                    return true;
                }, CancellationToken, mode: SqlAccessMode.ReadOnly);
                if (attemptMutation)
                {
                    var error = await Assert.ThrowsExactlyAsync<PostgresException>(() => reading);
                    Assert.AreEqual("25006", error.SqlState);
                }
                else
                {
                    await reading;
                }
                Assert.ThrowsExactly<InvalidOperationException>(() => _ = Context.Sql);
                await Context.ExecuteBatchAsync(async (ctx, ct) =>
                {
                    Assert.AreEqual("off", await ctx.ReadSql.ExecuteScalarAsync(new SqlStatement { Statement = "SELECT current_setting('transaction_read_only')" }, ct));
                    return await ctx.Sql.ExecuteAsync(new SqlStatement { Statement = "INSERT INTO \"ServiceExecuteReadProbe\" (\"Id\") VALUES (1)" }, ct);
                }, CancellationToken);
                return new AccountResult(Parameters.AccountId, "written");
            }
        }

        private sealed class SystemBatchService(bool requireModule = false) : SystemService<AccountParameters, AccountData>
        {
            public int HookCalls { get; private set; }
            protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
            protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<AccountResult>()];
            protected override ModuleRequirement GetInstanceModules(AccountParameters parameters) => requireModule ? ModuleRequirement.All("Library") : ModuleRequirement.None;
            protected override ServicePermission GetPermission(AccountParameters parameters, Type resultType) => new("account.system.execute");

            protected override async Task<IServiceOutput> OnExecute()
            {
                HookCalls++;
                // A long Execute operation must never inherit a database transaction.
                Assert.ThrowsExactly<InvalidOperationException>(() => _ = Context.Sql);

                await Context.ExecuteBatchAsync(async (logic, token) =>
                {
                    await logic.Sql.ExecuteAsync(new SqlStatement { Statement = "INSERT INTO \"ServiceExecuteBatchProbe\" (\"Id\") VALUES (1)" }, token);
                    return true;
                }, CancellationToken);

                await Context.ExecuteBatchAsync(async (logic, token) =>
                {
                    await logic.Sql.ExecuteAsync(new SqlStatement { Statement = "INSERT INTO \"ServiceExecuteBatchProbe\" (\"Id\") VALUES (2)" }, token);
                    await logic.Sql.ExecuteAsync(new SqlStatement { Statement = "INSERT INTO \"ServiceExecuteBatchProbe\" (\"Id\") VALUES (2)" }, token);
                    return true;
                }, CancellationToken);

                return new AccountResult(Parameters.AccountId, Data.Name);
            }
        }

        private sealed class AccountReadService : UserService<AccountParameters>
        {
            protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<AccountResult>()];

            protected override ModuleRequirement GetInstanceModules(AccountParameters parameters) => ModuleRequirement.None;

            protected override ServicePermission GetPermission(AccountParameters parameters, Type resultType) => new("account.read");


            protected override async Task<IServiceOutput> OnRead()
            {
                var mode = await Context.ReadSql.ExecuteScalarAsync(new SqlStatement { Statement = "SELECT current_setting('transaction_read_only')" }, CancellationToken);
                Assert.AreEqual(1L, Auth.AccountId);
                Assert.AreEqual(2L, Auth.ProfileId);
                return new AccountResult(Parameters.AccountId, (string)mode!);
            }
        }

        private sealed class AccountWriteService(AccountWorkflowLogic logic) : AdminService<AccountParameters, AccountData>
        {
            protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;

            protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<AccountResult>()];

            protected override ModuleRequirement GetInstanceModules(AccountParameters parameters) => ModuleRequirement.None;

            protected override ServicePermission GetPermission(AccountParameters parameters, Type resultType) => new("account.update");


            protected override async Task<IServiceOutput> OnUpdate()
            {
                await logic.CreateAsync(Context, Parameters, Data, CancellationToken);
                await logic.UpdateAsync(Context, Parameters, Data, CancellationToken);

                if (Data.Duplicate)
                {
                    await logic.CreateAsync(Context, Parameters, Data, CancellationToken);
                }

                return new AccountResult(Parameters.AccountId, Data.Name);
            }
        }

        private sealed class AccountWorkflowLogic
        {
            public Task<int> CreateAsync(LogicContext context, AccountParameters parameters, AccountData data, CancellationToken cancellationToken)
                => context.Sql.ExecuteAsync(new SqlStatement { Statement = "INSERT INTO \"ServiceFoundationProbe\" (\"Id\", \"Name\") VALUES (@AccountId, @Name)", Parameters = { parameters, data } }, cancellationToken);

            public Task<int> UpdateAsync(LogicContext context, AccountParameters parameters, AccountData data, CancellationToken cancellationToken)
                => context.Sql.ExecuteAsync(new SqlStatement { Statement = "UPDATE \"ServiceFoundationProbe\" SET \"Name\" = @Name WHERE \"Id\" = @AccountId", Parameters = { parameters, data } }, cancellationToken);
        }

        private sealed class TestGate : IServiceGate
        {
            public readonly List<string> Events = [];
            public readonly List<SqlAccessMode> TransactionModes = [];
            public bool BlockModule { get; init; }
            public bool BlockSecondTransaction { get; init; }
            private int _transactionChecks;
            public ServiceArea Area { get; init; } = ServiceArea.User;

            public Task RequireModulesAsync(ModuleRequirement modules, LogicContext context, bool canMutate, CancellationToken cancellationToken)
            {
                Events.Add("modules");
                if (BlockModule)
                {
                    throw new InvalidOperationException("The module is disabled.");
                }

                return Task.CompletedTask;
            }

            public Task<ServiceCaller> RequireCallerAsync(ServiceArea area, CancellationToken cancellationToken)
            {
                Events.Add("caller");
                return Task.FromResult(new ServiceCaller(1, 2, Area, "test-user"));
            }

            public Task RequireAccessAsync(ServiceCaller caller, ServicePermission permission, CancellationToken cancellationToken)
            {
                Events.Add("access");
                return Task.CompletedTask;
            }

            public Task RequireTransactionalAccessAsync(ServiceCaller caller, ServicePermission permission, LogicContext context, CancellationToken cancellationToken, SqlAccessMode mode = SqlAccessMode.ReadWrite)
            {
                Events.Add("transaction");
                TransactionModes.Add(mode);
                Assert.AreEqual("test-user", caller.PrincipalKey);
                if (BlockSecondTransaction && ++_transactionChecks >= 2)
                {
                    throw new UnauthorizedAccessException("Resource permission revoked between Execute batches.");
                }

                return Task.CompletedTask;
            }
        }

        private static NpgsqlDataSource CreateDataSource()
        {
            var connectionString = TestPostgres.ResolveConnectionString($"Data Source=service-foundation-{Guid.NewGuid():N}");
            return new NpgsqlDataSourceBuilder(new NpgsqlConnectionStringBuilder(connectionString) { Pooling = true }.ConnectionString).Build();
        }
    }
}
