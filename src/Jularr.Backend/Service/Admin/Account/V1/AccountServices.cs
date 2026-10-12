using System.Data;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Account.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Account;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Account;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.Admin.Account.V1
{
    [ServiceEndpoint("GET", "/api/v1/admin/accounts")]
    public sealed class ReadAccountsV1 : AdminService<ReadAccountsParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<PageResult<AccountDetailsV1>>()];
        protected override ModuleRequirement GetInstanceModules(ReadAccountsParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(ReadAccountsParametersV1 parameters, Type resultType) => new("accounts.read");
        protected override PageRequest GetPageRequest(ReadAccountsParametersV1 parameters) => new(parameters.Page, parameters.PageSize);

        protected override async Task<IServiceOutput> OnRead()
        {
            // Bounded account directory.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    account."Id",
                    account."Email",
                    account."DisplayName",
                    account."AccountRoleTypeId",
                    account."IsEnabled",
                    account."EmailVerifiedAt",
                    account."UpdatedAt"
                FROM
                    "Accounts" AS account
                ORDER BY
                    account."DisplayName",
                    account."Id"
                LIMIT @PageSize
                OFFSET @Offset;
                """;
            sql.Page = Page;
            return await Context.ReadSql.ReadPageAsync<AccountDetailsV1>(sql, CancellationToken);
        }
    }

    [ServiceEndpoint("GET", "/api/v1/admin/accounts/{accountId:long}")]
    public sealed class ReadAccountV1 : AdminService<AccountParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<AccountDetailsV1>()];
        protected override ModuleRequirement GetInstanceModules(AccountParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(AccountParametersV1 parameters, Type resultType) => new("accounts.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            // Keep credentials private.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    account."Id",
                    account."Email",
                    account."DisplayName",
                    account."AccountRoleTypeId",
                    account."IsEnabled",
                    account."EmailVerifiedAt",
                    account."UpdatedAt"
                FROM
                    "Accounts" AS account
                WHERE
                    account."Id" = @AccountId;
                """;
            sql.Parameters.Add(Parameters);
            return await Context.ReadRequiredAsync<AccountDetailsV1>(sql, CancellationToken);
        }
    }

    [ServiceEndpoint("PUT", "/api/v1/admin/accounts/{accountId:long}")]
    public sealed class UpdateAccountV1 : AdminService<AccountParametersV1, UpdateAccountDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(AccountParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(AccountParametersV1 parameters, Type resultType) => new("accounts.update");
        protected override IsolationLevel? GetTransaction(AccountParametersV1 parameters) => IsolationLevel.Serializable;

        protected override async Task<IServiceOutput> OnUpdate()
        {
            // Complete account replacement.
            var data = new AccountUpdateData(Parameters.AccountId, Data.DisplayName, Data.AccountRoleTypeId, Data.IsEnabled);
            await Context.UpdateAccountAsync(data, CancellationToken);
            return new UpdateResult(true);
        }
    }
}
