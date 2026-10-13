using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Instance.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Media;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.Admin.Instance.V1
{
    [ServiceEndpoint("GET", "/api/v1/admin/instance/media")]
    public sealed class ReadInstanceMediaV1 : AdminService<ReadInstanceMediaParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<InstanceMediaDetailsV1>>()];
        protected override ModuleRequirement GetInstanceModules(ReadInstanceMediaParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(ReadInstanceMediaParametersV1 parameters, Type resultType) => new("instance.settings.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            // An enabled category needs a library of its own.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    category."Key",
                    category."MediaSubtypeId" IS NOT NULL AS "IsSubtype",
                    category."IsEnabled",
                    EXISTS (
                        SELECT
                            1
                        FROM
                            "LibraryRoots" AS root
                        WHERE
                            root."MediaSubtypeId" = category."MediaSubtypeId"
                            OR root."MediaTypeId" = category."MediaTypeId" AND root."MediaSubtypeId" IS NULL
                    ) AS "HasLibrary",
                    category."Version"
                FROM
                    "MediaCategories" AS category
                ORDER BY
                    category."Position";
                """;
            return new BaseDataList<InstanceMediaDetailsV1>(await Context.ReadSql.ReadListAsync<InstanceMediaDetailsV1>(sql, CancellationToken));
        }
    }

    [ServiceEndpoint("PUT", "/api/v1/admin/instance/media")]
    public sealed class UpdateInstanceMediaV1 : AdminService<UpdateInstanceMediaParametersV1, UpdateInstanceMediaDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(UpdateInstanceMediaParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(UpdateInstanceMediaParametersV1 parameters, Type resultType) => new("instance.settings.update");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            await Context.UpdateMediaCategoryAsync(new MediaCategoryUpdateData(Data.Key, Data.IsEnabled, Data.Version), CancellationToken);
            return new UpdateResult(true);
        }
    }
}
