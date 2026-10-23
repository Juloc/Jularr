using Jularr.Backend.Data;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Data.User.Profile.V1;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Media;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.User.Profile.V1
{
    [ServiceEndpoint("GET", "/api/v1/profiles/{profileId:long}/media")]
    public sealed class ReadProfileMediaV1 : UserService<ReadProfileParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<ProfileMediaV1>>()];
        protected override ModuleRequirement GetInstanceModules(ReadProfileParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(ReadProfileParametersV1 parameters, Type resultType) => new("profile.self.read");

        protected override Task ValidatePermissions(ReadProfileParametersV1 parameters) =>
            parameters.ProfileId == Auth.ProfileId ? Task.CompletedTask : throw new KeyNotFoundException("The authorized profile was not found.");

        protected override async Task<IServiceOutput> OnRead()
        {
            // Available categories the Owner did not block.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    category."Key",
                    category."MediaSubtypeId" IS NOT NULL AS "IsSubtype",
                    category."IsHidden"
                FROM
                    "ProfileMediaCategories" AS category
                WHERE
                    category."ProfileId" = @ProfileId
                ORDER BY
                    category."Position";
                """;
            sql.Parameters.Add(Parameters);
            return new BaseDataList<ProfileMediaV1>(await Context.ReadSql.ReadListAsync<ProfileMediaV1>(sql, CancellationToken));
        }
    }

    [ServiceEndpoint("PUT", "/api/v1/profiles/{profileId:long}/media")]
    public sealed class UpdateProfileMediaV1 : UserService<UpdateProfileParametersV1, UpdateProfileMediaDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(UpdateProfileParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(UpdateProfileParametersV1 parameters, Type resultType) => new("profile.self.update");

        protected override Task ValidatePermissions(UpdateProfileParametersV1 parameters) =>
            parameters.ProfileId == Auth.ProfileId ? Task.CompletedTask : throw new KeyNotFoundException("The authorized profile was not found.");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            await Context.SaveProfileMediaRulesAsync(new ProfileMediaRulesData(Parameters.ProfileId, null, Data.HiddenKeys), CancellationToken);
            return new UpdateResult(true);
        }
    }
}
