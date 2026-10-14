using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Library.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Library;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Library;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.Admin.Library.V1
{
    [ServiceEndpoint("GET", "/api/v1/admin/library/replacement")]
    public sealed class ReadReplacementSettingsV1 : AdminService<ReplacementSettingsParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<ReplacementSettingsV1>()];
        protected override ModuleRequirement GetInstanceModules(ReplacementSettingsParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(ReplacementSettingsParametersV1 parameters, Type resultType) => new("storage.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    settings."KeepsReleases",
                    settings."RecyclesInLibraries",
                    settings."RecycleStorageMountId",
                    settings."RecyclePath",
                    settings."RecycleDays",
                    settings."Version"
                FROM
                    "InstanceSettings" AS settings
                WHERE
                    settings."Id" = 1;
                """;
            return await Context.ReadSql.ReadRequiredAsync<ReplacementSettingsV1>(sql, CancellationToken);
        }
    }

    [ServiceEndpoint("PUT", "/api/v1/admin/library/replacement")]
    public sealed class UpdateReplacementSettingsV1 : AdminService<ReplacementSettingsParametersV1, UpdateReplacementSettingsDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(ReplacementSettingsParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(ReplacementSettingsParametersV1 parameters, Type resultType) => new("storage.update");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            await Context.SaveReplacementSettingsAsync(new ReplacementSettingsSaveData(Data.KeepsReleases, Data.RecyclesInLibraries, Data.RecycleStorageMountId, Data.RecyclePath,
                Data.RecycleDays, Data.Version), CancellationToken);
            return new UpdateResult(true);
        }
    }
}
