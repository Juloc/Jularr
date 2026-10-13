using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Account.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Media;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.Admin.Account.V1
{
    [ServiceEndpoint("GET", "/api/v1/admin/accounts/{accountId:long}/profiles/media")]
    public sealed class ReadProfileMediaAccessV1 : AdminService<AccountProfileMediaParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<ProfileMediaAccessV1>>()];
        protected override ModuleRequirement GetInstanceModules(AccountProfileMediaParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(AccountProfileMediaParametersV1 parameters, Type resultType) => new("accounts.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            // Available categories per profile of the account.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    access."ProfileId",
                    category."Key",
                    category."MediaSubtypeId" IS NOT NULL AS "IsSubtype",
                    COALESCE(rule."IsBlocked", FALSE) AS "IsBlocked"
                FROM
                    "AccountProfiles" AS access
                    CROSS JOIN "MediaCategories" AS category
                    LEFT JOIN "ProfileMediaRules" AS rule ON rule."ProfileId" = access."ProfileId"
                        AND rule."MediaTypeId" IS NOT DISTINCT FROM category."MediaTypeId"
                        AND rule."MediaSubtypeId" IS NOT DISTINCT FROM category."MediaSubtypeId"
                WHERE
                    access."AccountId" = @AccountId
                    AND category."IsAvailable"
                ORDER BY
                    access."ProfileId",
                    category."Position";
                """;
            sql.Parameters.Add(Parameters);
            return new BaseDataList<ProfileMediaAccessV1>(await Context.ReadSql.ReadListAsync<ProfileMediaAccessV1>(sql, CancellationToken));
        }
    }

    [ServiceEndpoint("PUT", "/api/v1/admin/profiles/{profileId:long}/media")]
    public sealed class UpdateProfileMediaAccessV1 : AdminService<ProfileMediaAccessParametersV1, UpdateProfileMediaAccessDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(ProfileMediaAccessParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(ProfileMediaAccessParametersV1 parameters, Type resultType) => new("accounts.update");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            await Context.SaveProfileMediaRulesAsync(new ProfileMediaRulesData(Parameters.ProfileId, Data.BlockedKeys, null), CancellationToken);
            return new UpdateResult(true);
        }
    }
}
