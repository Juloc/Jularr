using Jularr.Backend.Data.Enums.Sql;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Service.Core;
using Jularr.Backend.Tests.Infrastructure;
using Jularr.Backend.Data;
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
            var runtime = new ServiceRuntime(database, gate);
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
            var service = new ServiceRuntime(database, gate).Create(new AccountReadService());

            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => service.ExecuteAsync<AccountResult>(new AccountParameters(5)));

            CollectionAssert.AreEqual(new[] { "caller", "access", "modules" }, gate.Events.ToArray());
        }

        [TestMethod]
        public async Task UndeclaredOutput_IsRejectedWithoutInvokingGate()
        {
            await using var database = CreateDataSource();
            var gate = new TestGate();
            var service = new ServiceRuntime(database, gate).Create(new AccountReadService());

            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => service.ExecuteAsync<PrivateAccountResult>(new AccountParameters(5)));

            Assert.AreEqual(0, gate.Events.Count);
        }

        [TestMethod]
        public async Task UserList_InvalidSortAndPage_FailBeforeCallerOrDatabase()
        {
            await using var database = CreateDataSource();
            var gate = new TestGate();
            var service = new ServiceRuntime(database, gate).Create(new AccountListService());

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
            var service = new ServiceRuntime(database, new TestGate()).Create(new AccountListService());

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
            var service = new ServiceRuntime(database, gate).Create(new AccountWriteService(new AccountWorkflowLogic()));

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
            var service = new ServiceRuntime(database, gate).Create(new SystemBatchService());

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
            var service = new ServiceRuntime(database, gate).Create(new SystemBatchService());

            await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(
                () => service.ExecuteAsync<AccountResult>(new AccountParameters(9), new AccountData("blocked")));

            await using var verify = new SqlContext(database);
            await verify.BeginAsync(SqlAccessMode.ReadOnly);
            Assert.AreEqual(1L, await verify.ReadSql.ExecuteScalarAsync(new SqlStatement { Statement = "SELECT COUNT(*) FROM \"ServiceExecuteBatchProbe\"" }));
            CollectionAssert.AreEqual(new[] { "caller", "access", "modules", "transaction", "transaction" }, gate.Events.ToArray());
        }

        [TestMethod]
        public async Task ReadService_CannotObtainLogicWriteCapability()
        {
            await using var database = CreateDataSource();
            var service = new ServiceRuntime(database, new TestGate()).Create(new ReadWithForbiddenLogicService());

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
            var service = new ServiceRuntime(database, gate).Create(new HookReadService());

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
            var service = new ServiceRuntime(database, new TestGate()).Create(new HookReadService());
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
        public async Task Execute_DisabledModule_NeverRunsOperationHook()
        {
            await using var database = CreateDataSource();
            var gate = new TestGate { Area = ServiceArea.System, BlockModule = true };
            var service = new ServiceRuntime(database, gate).Create(new SystemBatchService(requireModule: true));

            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.ExecuteAsync<AccountResult>(new AccountParameters(9), new AccountData("blocked")));
            Assert.AreEqual(0, service.HookCalls);
            CollectionAssert.AreEqual(new[] { "caller", "access", "transaction", "modules" }, gate.Events.ToArray());
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

            public Task RequireTransactionalAccessAsync(ServiceCaller caller, ServicePermission permission, LogicContext context, CancellationToken cancellationToken)
            {
                Events.Add("transaction");
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
