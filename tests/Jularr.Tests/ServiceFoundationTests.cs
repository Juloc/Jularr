using Jularr.Data.Common;
using Jularr.Infrastructure.Sql;
using Jularr.Service.Core;
using Jularr.Tests.Infrastructure;
using Jularr.Web.Data;
using Npgsql;

namespace Jularr.Tests;

[TestClass]
public sealed class ServiceFoundationTests
{
    private sealed record AccountParameters(long AccountId);

    private sealed record AccountData(string Name, bool Duplicate = false);

    private sealed record AccountResult(long AccountId, string Name) : IServiceOutput;

    private sealed record PrivateAccountResult(long AccountId, string Name, string Email) : IServiceOutput;

    [TestMethod]
    public async Task ReadService_RequiresModuleFirstAndUsesReadOnlyDatabase()
    {
        await using var database = CreateDataSource();
        var gate = new TestGate();
        var runtime = new ServiceRuntime(database, gate);
        var service = new AccountReadService(runtime);
        runtime.ValidateAll([service]);

        var result = await service.ExecuteAsync<AccountResult>(new AccountParameters(5));

        Assert.AreEqual(5L, result.AccountId);
        Assert.AreEqual("on", result.Name);
        CollectionAssert.AreEqual(new[] { "modules", "caller", "access" }, gate.Events.ToArray());
    }

    [TestMethod]
    public async Task ModulesBlocked_NeverOpensDatabaseOrResolvesCaller()
    {
        await using var database = CreateDataSource();
        var gate = new TestGate { BlockModule = true };
        var service = new AccountReadService(new ServiceRuntime(database, gate));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => service.ExecuteAsync<AccountResult>(new AccountParameters(5)));

        CollectionAssert.AreEqual(new[] { "modules" }, gate.Events.ToArray());
    }

    [TestMethod]
    public async Task UndeclaredOutput_IsRejectedWithoutInvokingGate()
    {
        await using var database = CreateDataSource();
        var gate = new TestGate();
        var service = new AccountReadService(new ServiceRuntime(database, gate));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => service.ExecuteAsync<PrivateAccountResult>(new AccountParameters(5)));

        CollectionAssert.AreEqual(new[] { "modules" }, gate.Events.ToArray());
    }

    [TestMethod]
    public async Task UserList_InvalidSortAndPage_FailBeforeCallerOrDatabase()
    {
        await using var database = CreateDataSource();
        var gate = new TestGate();
        var service = new AccountListService(new ServiceRuntime(database, gate));

        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(
            () => service.ExecuteAsync<AccountResult>(new ListParameters(1, 25, (ListSort)255)));
        CollectionAssert.AreEqual(new[] { "modules" }, gate.Events.ToArray());

        gate.Events.Clear();
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(
            () => service.ExecuteAsync<AccountResult>(new ListParameters(1, 101, ListSort.AccountId)));
        CollectionAssert.AreEqual(new[] { "modules" }, gate.Events.ToArray());
    }

    [TestMethod]
    public async Task AdminWrite_MultipleLogicCallsRollbackOnLateFailure()
    {
        await using var database = CreateDataSource();
        await using (var seed = new SqlContext(database))
        {
            await seed.BeginAsync(SqlAccessMode.ReadWrite);
            await seed.RequireLogicSql().ExecuteAsync("CREATE TABLE \"ServiceFoundationProbe\" (\"Id\" bigint PRIMARY KEY, \"Name\" text NOT NULL)");
            await seed.CommitAsync();
        }

        var gate = new TestGate { Area = ServiceArea.Admin };
        var service = new AccountWriteService(new ServiceRuntime(database, gate), new AccountWorkflowLogic());

        var error = await Assert.ThrowsExactlyAsync<PostgresException>(
            () => service.ExecuteAsync<AccountResult>(new AccountParameters(5), new AccountData("test", Duplicate: true)));
        Assert.AreEqual("23505", error.SqlState);

        await using var verify = new SqlContext(database);
        await verify.BeginAsync(SqlAccessMode.ReadOnly);
        Assert.AreEqual(0L, await verify.ReadSql.ExecuteScalarAsync("SELECT COUNT(*) FROM \"ServiceFoundationProbe\""));
        CollectionAssert.AreEqual(new[] { "modules", "caller", "access", "transaction" }, gate.Events.ToArray());
    }

    private enum ListSort : byte
    {
        AccountId,
        DisplayName
    }

    private sealed record ListParameters(int Page, int PageSize, ListSort? Sort);

    private sealed class AccountListService(ServiceRuntime runtime) : UserListService<ListParameters, ListSort>(runtime)
    {
        protected override IReadOnlyList<ServiceSortKey<ListSort>> GetSortKeys() =>
        [
            ServiceSortKey<ListSort>.Default(ListSort.AccountId),
            ServiceSortKey<ListSort>.Additional(ListSort.DisplayName)
        ];

        protected override ListSort? GetRequestedSort(ListParameters parameters) => parameters.Sort;
        protected override PageRequest GetPageRequest(ListParameters parameters) => new(parameters.Page, parameters.PageSize);
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<AccountResult>()];
        protected override ModuleRequirement GetInstanceModules(ListParameters parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(ListParameters parameters, Type resultType) => new("accounts.list");
        protected override ResourceTarget? GetResource(ListParameters parameters) => null;

        protected override Task<IServiceOutput> ExecuteCoreAsync(ListParameters parameters, NoData data, Type resultType, ServiceContext context, CancellationToken cancellationToken)
            => Task.FromResult<IServiceOutput>(new AccountResult(1, "test"));
    }

    private sealed class AccountReadService(ServiceRuntime runtime) : UserReadService<AccountParameters>(runtime)
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<AccountResult>()];

        protected override ModuleRequirement GetInstanceModules(AccountParameters parameters) => ModuleRequirement.None;

        protected override ServicePermission GetPermission(AccountParameters parameters, Type resultType) => new("account.read");

        protected override ResourceTarget? GetResource(AccountParameters parameters) => new("Account", parameters.AccountId);

        protected override async Task<IServiceOutput> ExecuteCoreAsync(AccountParameters parameters, NoData data, Type resultType, ServiceContext context, CancellationToken cancellationToken)
        {
            var mode = await context.ReadSql.ExecuteScalarAsync("SELECT current_setting('transaction_read_only')", cancellationToken);
            var actorId = await context.ReadSql.ExecuteScalarAsync("SELECT @ActorAccountId::bigint", cancellationToken);
            var profileId = await context.ReadSql.ExecuteScalarAsync("SELECT @ActiveProfileId::bigint", cancellationToken);

            Assert.AreEqual(1L, actorId);
            Assert.AreEqual(2L, profileId);
            return new AccountResult(parameters.AccountId, (string)mode!);
        }
    }

    private sealed class AccountWriteService(ServiceRuntime runtime, AccountWorkflowLogic logic) : AdminService<AccountParameters, AccountData>(runtime)
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;

        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<AccountResult>()];

        protected override ModuleRequirement GetInstanceModules(AccountParameters parameters) => ModuleRequirement.None;

        protected override ServicePermission GetPermission(AccountParameters parameters, Type resultType) => new("account.update");

        protected override ResourceTarget? GetResource(AccountParameters parameters) => new("Account", parameters.AccountId);

        protected override async Task<IServiceOutput> ExecuteCoreAsync(AccountParameters parameters, AccountData data, Type resultType, ServiceContext context, CancellationToken cancellationToken)
        {
            await logic.CreateAsync(context.Logic, parameters, data, cancellationToken);
            await logic.UpdateAsync(context.Logic, parameters, data, cancellationToken);

            if (data.Duplicate)
            {
                await logic.CreateAsync(context.Logic, parameters, data, cancellationToken);
            }

            return new AccountResult(parameters.AccountId, data.Name);
        }
    }

    private sealed class AccountWorkflowLogic
    {
        public Task<int> CreateAsync(LogicContext context, AccountParameters parameters, AccountData data, CancellationToken cancellationToken)
            => context.Sql.ExecuteAsync("INSERT INTO \"ServiceFoundationProbe\" (\"Id\", \"Name\") VALUES (@AccountId, @Name)", parameters, data, cancellationToken);

        public Task<int> UpdateAsync(LogicContext context, AccountParameters parameters, AccountData data, CancellationToken cancellationToken)
            => context.Sql.ExecuteAsync("UPDATE \"ServiceFoundationProbe\" SET \"Name\" = @Name WHERE \"Id\" = @AccountId", parameters, data, cancellationToken);
    }

    private sealed class TestGate : IServiceGate
    {
        public readonly List<string> Events = [];
        public bool BlockModule { get; init; }
        public ServiceArea Area { get; init; } = ServiceArea.User;

        public Task RequireModulesAsync(ModuleRequirement modules, CancellationToken cancellationToken)
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

        public Task RequireAccessAsync(ServiceCaller caller, ServicePermission permission, IReadOnlyList<ResourceTarget> targets, CancellationToken cancellationToken)
        {
            Events.Add("access");
            Assert.AreEqual(1, targets.Count);
            Assert.AreEqual("Account", targets[0].Kind);
            return Task.CompletedTask;
        }

        public Task RequireTransactionalAccessAsync(ServiceCaller caller, ServicePermission permission, IReadOnlyList<ResourceTarget> targets, LogicContext context, CancellationToken cancellationToken)
        {
            Events.Add("transaction");
            Assert.AreEqual("test-user", caller.PrincipalKey);
            return Task.CompletedTask;
        }
    }

    private static NpgsqlDataSource CreateDataSource()
    {
        var connectionString = TestPostgres.ResolveConnectionString($"Data Source=service-foundation-{Guid.NewGuid():N}");
        return new NpgsqlDataSourceBuilder(new NpgsqlConnectionStringBuilder(connectionString) { Pooling = true }.ConnectionString).Build();
    }
}
