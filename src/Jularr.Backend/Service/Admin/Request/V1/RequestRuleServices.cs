using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Request.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Acquisition;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.Admin.Request.V1
{
    [ServiceEndpoint("GET", "/api/v1/admin/requests/rules")]
    public sealed class ReadRequestRulesV1 : AdminService<ReadRequestRulesParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<RequestRuleDetailsV1>>()];
        protected override ModuleRequirement GetInstanceModules(ReadRequestRulesParametersV1 parameters) => ModuleRequirement.All("Requests");
        protected override ServicePermission GetPermission(ReadRequestRulesParametersV1 parameters, Type resultType) => new("rules.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            // The default first; allowed media are the categories a rule does not block.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    rule."Id",
                    rule."Name",
                    rule."Description",
                    rule."RequestLimit",
                    rule."PeriodDays",
                    rule."IsAutomaticApproval",
                    ARRAY(SELECT category."Key" FROM "MediaCategories" AS category WHERE NOT category."Key" = ANY(rule."BlockedMediaKeys") ORDER BY category."Position") AS "AllowedMediaKeys",
                    rule."AcquisitionProfileIds",
                    rule."IsDefault",
                    (SELECT count(*) FROM "AccountRequestRules" AS assignment WHERE assignment."RequestRuleId" = rule."Id") AS "AccountCount",
                    rule."Version"
                FROM
                    "RequestRules" AS rule
                ORDER BY
                    rule."IsDefault" DESC,
                    rule."Name";
                """;
            return new BaseDataList<RequestRuleDetailsV1>(await Context.ReadSql.ReadListAsync<RequestRuleDetailsV1>(sql, CancellationToken));
        }
    }

    [ServiceEndpoint("POST", "/api/v1/admin/requests/rules")]
    public sealed class CreateRequestRuleV1 : AdminService<ReadRequestRulesParametersV1, SaveRequestRuleDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Create;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<CreateRequestRuleResultV1>()];
        protected override ModuleRequirement GetInstanceModules(ReadRequestRulesParametersV1 parameters) => ModuleRequirement.All("Requests");
        protected override ServicePermission GetPermission(ReadRequestRulesParametersV1 parameters, Type resultType) => new("rules.update");

        protected override async Task<IServiceOutput> OnCreate() =>
            new CreateRequestRuleResultV1(await Context.CreateRequestRuleAsync(new RequestRuleSaveData(null, Data.Name, Data.Description, Data.RequestLimit, Data.PeriodDays,
                Data.IsAutomaticApproval, Data.AllowedMediaKeys, Data.AcquisitionProfileIds, Data.IsDefault, null), CancellationToken));
    }

    [ServiceEndpoint("PUT", "/api/v1/admin/requests/rules/{requestRuleId:long}")]
    public sealed class UpdateRequestRuleV1 : AdminService<RequestRuleParametersV1, SaveRequestRuleDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(RequestRuleParametersV1 parameters) => ModuleRequirement.All("Requests");
        protected override ServicePermission GetPermission(RequestRuleParametersV1 parameters, Type resultType) => new("rules.update");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            var version = Data.Version ?? throw new DataValidationException(nameof(Data.Version), "required");
            await Context.UpdateRequestRuleAsync(new RequestRuleSaveData(Parameters.RequestRuleId, Data.Name, Data.Description, Data.RequestLimit, Data.PeriodDays,
                Data.IsAutomaticApproval, Data.AllowedMediaKeys, Data.AcquisitionProfileIds, Data.IsDefault, version), CancellationToken);
            return new UpdateResult(true);
        }
    }

    [ServiceEndpoint("DELETE", "/api/v1/admin/requests/rules/{requestRuleId:long}")]
    public sealed class DeleteRequestRuleV1 : AdminService<DeleteRequestRuleParametersV1, NoData>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Delete;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(DeleteRequestRuleParametersV1 parameters) => ModuleRequirement.All("Requests");
        protected override ServicePermission GetPermission(DeleteRequestRuleParametersV1 parameters, Type resultType) => new("rules.update");

        protected override async Task<IServiceOutput> OnDelete()
        {
            await Context.DeleteRequestRuleAsync(new RequestRuleKeyData(Parameters.RequestRuleId, Parameters.Version), CancellationToken);
            return new UpdateResult(true);
        }
    }

    [ServiceEndpoint("GET", "/api/v1/admin/requests/users")]
    public sealed class ReadRequestUsersV1 : AdminService<ReadRequestUsersParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<PageResult<AccountRequestRuleV1>>()];
        protected override ModuleRequirement GetInstanceModules(ReadRequestUsersParametersV1 parameters) => ModuleRequirement.All("Requests");
        protected override ServicePermission GetPermission(ReadRequestUsersParametersV1 parameters, Type resultType) => new("rules.read");
        protected override PageRequest GetPageRequest(ReadRequestUsersParametersV1 parameters) => new(parameters.Page, parameters.PageSize);

        protected override async Task<IServiceOutput> OnRead()
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    account."Id" AS "AccountId",
                    account."DisplayName",
                    account."AccountRoleTypeId",
                    rule."RequestRuleId",
                    rule."RequestRuleName",
                    rule."IsInherited",
                    rule."IsOverridden",
                    rule."RequestLimit",
                    rule."PeriodDays",
                    rule."IsAutomaticApproval",
                    ARRAY(SELECT category."Key" FROM "MediaCategories" AS category WHERE NOT category."Key" = ANY(rule."BlockedMediaKeys") ORDER BY category."Position") AS "AllowedMediaKeys",
                    rule."AcquisitionProfileIds"
                FROM
                    "Accounts" AS account
                    JOIN "EffectiveRequestRules" AS rule ON rule."AccountId" = account."Id"
                ORDER BY
                    account."DisplayName",
                    account."Id"
                LIMIT @PageSize
                OFFSET @Offset;
                """;
            sql.Page = Page;
            return await Context.ReadSql.ReadPageAsync<AccountRequestRuleV1>(sql, CancellationToken);
        }
    }

    [ServiceEndpoint("GET", "/api/v1/admin/accounts/{accountId:long}/request-rule")]
    public sealed class ReadAccountRequestRuleV1 : AdminService<AccountRequestRuleParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<AccountRequestRuleV1>()];
        protected override ModuleRequirement GetInstanceModules(AccountRequestRuleParametersV1 parameters) => ModuleRequirement.All("Requests");
        protected override ServicePermission GetPermission(AccountRequestRuleParametersV1 parameters, Type resultType) => new("rules.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    account."Id" AS "AccountId",
                    account."DisplayName",
                    account."AccountRoleTypeId",
                    rule."RequestRuleId",
                    rule."RequestRuleName",
                    rule."IsInherited",
                    rule."IsOverridden",
                    rule."RequestLimit",
                    rule."PeriodDays",
                    rule."IsAutomaticApproval",
                    ARRAY(SELECT category."Key" FROM "MediaCategories" AS category WHERE NOT category."Key" = ANY(rule."BlockedMediaKeys") ORDER BY category."Position") AS "AllowedMediaKeys",
                    rule."AcquisitionProfileIds"
                FROM
                    "Accounts" AS account
                    JOIN "EffectiveRequestRules" AS rule ON rule."AccountId" = account."Id"
                WHERE
                    account."Id" = @AccountId;
                """;
            sql.Parameters.Add(Parameters);
            return await Context.ReadSql.ReadOptionalAsync<AccountRequestRuleV1>(sql, CancellationToken) ?? throw new KeyNotFoundException("The account was not found.");
        }
    }

    [ServiceEndpoint("PUT", "/api/v1/admin/accounts/{accountId:long}/request-rule")]
    public sealed class SaveAccountRequestRuleV1 : AdminService<AccountRequestRuleParametersV1, SaveAccountRequestRuleDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(AccountRequestRuleParametersV1 parameters) => ModuleRequirement.All("Requests");
        protected override ServicePermission GetPermission(AccountRequestRuleParametersV1 parameters, Type resultType) => new("rules.update");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            await Context.SaveAccountRequestRuleAsync(new AccountRequestRuleSaveData(Parameters.AccountId, Data.RequestRuleId, Data.IsOverridden, Data.RequestLimit, Data.PeriodDays,
                Data.IsAutomaticApproval, Data.AllowedMediaKeys, Data.AcquisitionProfileIds), CancellationToken);
            return new UpdateResult(true);
        }
    }
}
