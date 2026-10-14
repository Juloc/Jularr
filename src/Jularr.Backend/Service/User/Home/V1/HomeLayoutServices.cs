using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Profile;
using Jularr.Backend.Data.User.Home.V1;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Profile;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.User.Home.V1
{
    [ServiceEndpoint("GET", "/api/v1/home/layout")]
    public sealed class ReadHomeLayoutV1 : UserService<HomeLayoutParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<HomeShelfV1>>()];
        protected override ModuleRequirement GetInstanceModules(HomeLayoutParametersV1 parameters) => ModuleRequirement.All("Library", "HomeLayout");
        protected override ServicePermission GetPermission(HomeLayoutParametersV1 parameters, Type resultType) => new("profile.media.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            // The profile's order, else the default one.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    type."Id" AS "HomeShelfTypeId",
                    coalesce(shelf."IsHidden", FALSE) AS "IsHidden"
                FROM
                    "HomeShelfTypes" AS type
                    LEFT JOIN "ProfileHomeShelves" AS shelf ON shelf."HomeShelfTypeId" = type."Id" AND shelf."ProfileId" = @ProfileId
                ORDER BY
                    shelf."Position" NULLS LAST,
                    type."Id";
                """;
            sql.Parameters.Add(new ProfileParameters(Auth.ProfileId!.Value));
            return new BaseDataList<HomeShelfV1>(await Context.ReadSql.ReadListAsync<HomeShelfV1>(sql, CancellationToken));
        }
    }

    [ServiceEndpoint("PUT", "/api/v1/home/layout")]
    public sealed class SaveHomeLayoutV1 : UserService<HomeLayoutParametersV1, SaveHomeLayoutDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(HomeLayoutParametersV1 parameters) => ModuleRequirement.All("Library", "HomeLayout");
        protected override ServicePermission GetPermission(HomeLayoutParametersV1 parameters, Type resultType) => new("profile.media.read");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            await Context.SaveHomeLayoutAsync(new HomeLayoutSaveData(Auth.ProfileId!.Value, Data.ShelfKeys, Data.HiddenKeys), CancellationToken);
            return new UpdateResult(true);
        }
    }
}
